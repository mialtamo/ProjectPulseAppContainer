using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProjectPulse.Processor.Models;
using ProjectPulse.Processor.Options;
using ProjectPulse.Processor.Services;

namespace ProjectPulse.Processor.ContainerApp;

public sealed class ServiceBusWorker(
    ServiceBusClient serviceBusClient,
    IHealthGate healthGate,
    IClaimProcessor claimProcessor,
    ProcessingConcurrencyGates concurrencyGates,
    IAppApiClient appApiClient,
    IRetryQueue retryQueue,
    IRtaNotificationClient rtaNotificationClient,
    IPollBatchLedger pollBatchLedger,
    IOptions<ClaimProcessorOptions> options,
    TelemetryClient telemetry,
    ILogger<ServiceBusWorker> logger) : BackgroundService
{
    private readonly ClaimProcessorOptions _options = options.Value;
    private readonly ProcessingConcurrencyGates _concurrencyGates = concurrencyGates;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var receiver = serviceBusClient.CreateReceiver(
            _options.ServiceBusQueueName,
            new ServiceBusReceiverOptions
            {
                ReceiveMode = ServiceBusReceiveMode.PeekLock,
                PrefetchCount = 0
            });
        await using var deadLetterReceiver = serviceBusClient.CreateReceiver(
            _options.ServiceBusQueueName,
            new ServiceBusReceiverOptions
            {
                ReceiveMode = ServiceBusReceiveMode.PeekLock,
                PrefetchCount = 0,
                SubQueue = SubQueue.DeadLetter
            });
        await using var deadLetterReplaySender = serviceBusClient.CreateSender(_options.ServiceBusQueueName);

        var mode = _concurrencyGates.Mode;
        var maxConcurrency = _concurrencyGates.MaxConcurrentMessages;
        var inFlight = new List<Task>();
        var nextRtaHealthCheckUtc = DateTimeOffset.MinValue;
        var nextDeadLetterPollUtc = DateTimeOffset.MinValue;
        var isRtaHealthy = false;

        logger.LogInformation(
            "Project Pulse Service Bus worker started. Queue={Queue} MaxConcurrentMessages={MaxConcurrentMessages} ConcurrencyMode={ConcurrencyMode}",
            _options.ServiceBusQueueName,
            maxConcurrency,
            mode);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await ObserveCompletedTasksAsync(inFlight, stoppingToken);

                if (inFlight.Count >= maxConcurrency)
                {
                    await Task.WhenAny(inFlight);
                    continue;
                }

                try
                {
                    if (DateTimeOffset.UtcNow >= nextRtaHealthCheckUtc)
                    {
                        var rtaHealth = await healthGate.CheckRtaAsync(stoppingToken);
                        isRtaHealthy = rtaHealth.IsHealthy;

                        var healthCheckInterval = isRtaHealthy
                            ? _options.GetServiceBusReceiveWaitSeconds()
                            : _options.GetServiceBusDependencyRetrySeconds();
                        nextRtaHealthCheckUtc = DateTimeOffset.UtcNow.AddSeconds(healthCheckInterval);

                        if (!isRtaHealthy)
                        {
                            logger.LogWarning(
                                "Service Bus processing paused because RTA is unhealthy. RtaHealthy={RtaHealthy}",
                                rtaHealth.IsHealthy);
                        }
                    }

                    if (!isRtaHealthy)
                    {
                        await Task.Delay(
                            TimeSpan.FromSeconds(_options.GetServiceBusDependencyRetrySeconds()),
                            stoppingToken);

                        continue;
                    }

                    if (inFlight.Count < maxConcurrency && DateTimeOffset.UtcNow >= nextDeadLetterPollUtc)
                    {
                        nextDeadLetterPollUtc = DateTimeOffset.UtcNow.AddSeconds(_options.GetServiceBusReceiveWaitSeconds());
                        var deadLetterMessage = await deadLetterReceiver.ReceiveMessageAsync(
                            TimeSpan.FromMilliseconds(100),
                            stoppingToken);
                        if (deadLetterMessage is not null)
                        {
                            if (IsReplayableDeadLetterMessage(deadLetterMessage))
                            {
                                try
                                {
                                    await ReplayDeadLetterMessageAsync(
                                        deadLetterReceiver,
                                        deadLetterReplaySender,
                                        deadLetterMessage,
                                        stoppingToken);
                                    telemetry.TrackEvent("ServiceBusDeadLetterRecoveryStarted", new Dictionary<string, string>
                                    {
                                        ["messageId"] = deadLetterMessage.MessageId,
                                        ["deadLetterReason"] = deadLetterMessage.DeadLetterReason ?? "unknown",
                                        ["queue"] = _options.ServiceBusQueueName
                                    });
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException)
                                {
                                    logger.LogError(
                                        ex,
                                        "Could not return a recoverable dead-letter message to the active queue. MessageId={MessageId}",
                                        deadLetterMessage.MessageId);
                                    telemetry.TrackException(ex);
                                    await deadLetterReceiver.AbandonMessageAsync(
                                        deadLetterMessage,
                                        cancellationToken: stoppingToken);
                                }
                            }
                            else
                            {
                                logger.LogError(
                                    "Service Bus dead-letter message is not eligible for automatic recovery and remains available for support review. MessageId={MessageId} Reason={DeadLetterReason}",
                                    deadLetterMessage.MessageId,
                                    deadLetterMessage.DeadLetterReason ?? "unknown");
                                // Leave it parked in the dead-letter queue; repeated abandon would create a hot loop.
                                nextDeadLetterPollUtc = DateTimeOffset.UtcNow.AddMinutes(1);
                            }
                        }
                    }

                    if (inFlight.Count >= maxConcurrency)
                    {
                        continue;
                    }

                    var availableSlots = maxConcurrency - inFlight.Count;
                    var receiveCount = Math.Min(_options.GetServiceBusReceiveBatchSize(), availableSlots);
                    var messages = await receiver.ReceiveMessagesAsync(
                        receiveCount,
                        TimeSpan.FromSeconds(_options.GetServiceBusReceiveWaitSeconds()),
                        stoppingToken);

                    if (messages.Count == 0)
                    {
                        continue;
                    }

                    logger.LogInformation(
                        "Received {Count} Service Bus retry message(s). ActiveMessages={ActiveMessages} MaxConcurrentMessages={MaxConcurrentMessages}",
                        messages.Count,
                        inFlight.Count,
                        maxConcurrency);

                    foreach (var message in messages)
                    {
                        inFlight.Add(ProcessMessageAsync(receiver, message, stoppingToken));
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unhandled error in Service Bus worker loop");
                    telemetry.TrackException(ex);

                    await Task.Delay(
                        TimeSpan.FromSeconds(_options.GetServiceBusDependencyRetrySeconds()),
                        stoppingToken);
                }
            }
        }
        finally
        {
            if (inFlight.Count > 0)
            {
                try
                {
                    await Task.WhenAll(inFlight);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // In-flight messages keep their PeekLock and will be redelivered if shutdown interrupts processing.
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "One or more Service Bus message handlers ended with an error during shutdown");
                    telemetry.TrackException(ex);
                }
            }
        }

        logger.LogInformation("Project Pulse Service Bus worker stopped");
    }

    private async Task ObserveCompletedTasksAsync(List<Task> inFlight, CancellationToken cancellationToken)
    {
        for (var index = inFlight.Count - 1; index >= 0; index--)
        {
            var task = inFlight[index];
            if (!task.IsCompleted)
            {
                continue;
            }

            inFlight.RemoveAt(index);
            try
            {
                await task;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error while processing a Service Bus message");
                telemetry.TrackException(ex);
            }
        }
    }

    private static bool IsReplayableDeadLetterMessage(ServiceBusReceivedMessage message)
    {
        var reason = message.DeadLetterReason;
        var isRecoverableReason =
            string.Equals(reason, "TTLExpiredException", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(reason, "MaxDeliveryCountExceeded", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(reason, "RtaWritebackRetryWindowExpired", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(reason, "RtaWritebackRetryScheduleFailed", StringComparison.OrdinalIgnoreCase);
        if (!isRecoverableReason)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(message.Body.ToString());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("MessageType", out var messageType) &&
                messageType.ValueKind == JsonValueKind.String &&
                string.Equals(messageType.GetString(), RtaWritebackRetryEnvelope.TypeName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var envelope = JsonSerializer.Deserialize<RetryEnvelope>(message.Body.ToString());
            return envelope is not null && !string.IsNullOrWhiteSpace(envelope.OriginalPayload);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task ReplayDeadLetterMessageAsync(
        ServiceBusReceiver deadLetterReceiver,
        ServiceBusSender replaySender,
        ServiceBusReceivedMessage deadLetterMessage,
        CancellationToken cancellationToken)
    {
        var replay = new ServiceBusMessage(deadLetterMessage.Body)
        {
            ContentType = deadLetterMessage.ContentType,
            CorrelationId = deadLetterMessage.CorrelationId,
            Subject = deadLetterMessage.Subject,
            MessageId = $"{deadLetterMessage.MessageId}-recovered-{Guid.NewGuid():N}",
            TimeToLive = TimeSpan.FromMinutes(_options.GetServiceBusMessageRetentionMinutes())
        };

        foreach (var property in deadLetterMessage.ApplicationProperties)
        {
            replay.ApplicationProperties[property.Key] = property.Value;
        }

        replay.ApplicationProperties["recoveredFromDeadLetter"] = true;
        replay.ApplicationProperties["recoveredDeadLetterReason"] = deadLetterMessage.DeadLetterReason ?? "unknown";

        await replaySender.SendMessageAsync(replay, cancellationToken);
        await deadLetterReceiver.CompleteMessageAsync(deadLetterMessage, cancellationToken);

        logger.LogWarning(
            "Recovered Service Bus retry message to the active queue. MessageId={MessageId} DeadLetterReason={DeadLetterReason}",
            deadLetterMessage.MessageId,
            deadLetterMessage.DeadLetterReason ?? "unknown");
    }

    private async Task ProcessMessageAsync(
        ServiceBusReceiver receiver,
        ServiceBusReceivedMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            using var messageDocument = JsonDocument.Parse(message.Body.ToString());
            if (messageDocument.RootElement.ValueKind == JsonValueKind.Object &&
                messageDocument.RootElement.TryGetProperty("MessageType", out var messageType) &&
                messageType.ValueKind == JsonValueKind.String &&
                string.Equals(messageType.GetString(), "ServiceBusConnectivityTest", StringComparison.OrdinalIgnoreCase))
            {
                var testId = messageDocument.RootElement.TryGetProperty("TestId", out var testIdValue)
                    ? testIdValue.GetString()
                    : null;
                await receiver.CompleteMessageAsync(message, cancellationToken);
                telemetry.TrackEvent("ServiceBusConnectivityTestReceived", new Dictionary<string, string>
                {
                    ["testId"] = testId ?? "unknown",
                    ["queue"] = _options.ServiceBusQueueName
                });
                logger.LogInformation(
                    "Service Bus connectivity test message received and completed. TestId={TestId} Queue={Queue}",
                    testId ?? "unknown",
                    _options.ServiceBusQueueName);
                return;
            }

            if (messageDocument.RootElement.ValueKind == JsonValueKind.Object &&
                messageDocument.RootElement.TryGetProperty("MessageType", out messageType) &&
                messageType.ValueKind == JsonValueKind.String &&
                string.Equals(messageType.GetString(), RtaWritebackRetryEnvelope.TypeName, StringComparison.OrdinalIgnoreCase))
            {
                await ProcessRtaWritebackMessageAsync(receiver, message, messageDocument.RootElement, cancellationToken);
                return;
            }
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Invalid Service Bus message JSON. MessageId={MessageId}", message.MessageId);
            telemetry.TrackException(ex);
            await receiver.DeadLetterMessageAsync(message, "InvalidRetryEnvelope", "Message body was not valid JSON.", cancellationToken);
            return;
        }

        RetryEnvelope? envelope;

        try
        {
            envelope = JsonSerializer.Deserialize<RetryEnvelope>(message.Body.ToString());
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Invalid Service Bus retry envelope. MessageId={MessageId}", message.MessageId);
            telemetry.TrackException(ex);

            await receiver.DeadLetterMessageAsync(
                message,
                "InvalidRetryEnvelope",
                "Message body could not be deserialized as a Project Pulse RetryEnvelope.",
                cancellationToken);

            return;
        }

        if (envelope is null || string.IsNullOrWhiteSpace(envelope.OriginalPayload))
        {
            logger.LogError("Service Bus retry envelope is missing OriginalPayload. MessageId={MessageId}", message.MessageId);

            await receiver.DeadLetterMessageAsync(
                message,
                "InvalidRetryEnvelope",
                "RetryEnvelope was null or OriginalPayload was empty.",
                cancellationToken);

            return;
        }

        try
        {
            using var document = JsonDocument.Parse(envelope.OriginalPayload);
            var claim = document.RootElement.Clone();

            logger.LogInformation(
                "Retrying Service Bus claim {UniqueId}. DeliveryCount={DeliveryCount} OriginalReason={OriginalReason}",
                envelope.UniqueId,
                message.DeliveryCount,
                envelope.Reason);

            var results = await claimProcessor.ProcessBatchAsync([claim], [], cancellationToken, retryEnvelope: envelope);
            var result = results.SingleOrDefault();

            if (result is null)
            {
                throw new InvalidOperationException(
                    $"No processing result was returned for Service Bus claim '{envelope.UniqueId}'.");
            }

            switch (result.Disposition)
            {
                case ClaimProcessingDisposition.Completed:
                case ClaimProcessingDisposition.Alerted:
                // Processing queued a replacement envelope, so complete this delivery to avoid replaying it.
                case ClaimProcessingDisposition.QueuedForRetry:
                case ClaimProcessingDisposition.QueuedForWritebackRetry:
                    await receiver.CompleteMessageAsync(message, cancellationToken);

                    telemetry.TrackEvent("ServiceBusRetryProcessed", new Dictionary<string, string>
                    {
                        ["uniqueId"] = envelope.UniqueId,
                        ["disposition"] = result.Disposition.ToString(),
                        ["reason"] = result.Reason
                    });

                    logger.LogInformation(
                        "Service Bus claim {UniqueId} handled. Disposition={Disposition} Reason={Reason}",
                        envelope.UniqueId,
                        result.Disposition,
                        result.Reason);
                    break;

                case ClaimProcessingDisposition.FailedWriteback:
                    await receiver.DeadLetterMessageAsync(
                        message,
                        "RtaWritebackRetryCouldNotBeBuffered",
                        "APIM/DOCMP had already returned a result, but the exact RTA writeback could not be queued. APIM will not be replayed.",
                        cancellationToken);
                    logger.LogCritical(
                        "Dead-lettered claim retry {UniqueId} because its failed RTA writeback could not be buffered. APIM will not be replayed. Reason={Reason}",
                        envelope.UniqueId,
                        result.Reason);
                    break;

                case ClaimProcessingDisposition.Skipped:
                    await receiver.AbandonMessageAsync(message, cancellationToken: cancellationToken);

                    logger.LogWarning(
                        "Service Bus claim {UniqueId} was not completed. Disposition={Disposition} Reason={Reason}. Message will be retried.",
                        envelope.UniqueId,
                        result.Disposition,
                        result.Reason);
                    break;

                default:
                    await receiver.AbandonMessageAsync(message, cancellationToken: cancellationToken);
                    break;
            }
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "OriginalPayload is invalid JSON for Service Bus message {MessageId}", message.MessageId);
            telemetry.TrackException(ex);

            await receiver.DeadLetterMessageAsync(
                message,
                "InvalidOriginalPayload",
                "RetryEnvelope OriginalPayload was not valid JSON.",
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Service Bus retry processing failed for message {MessageId}. Message will be retried.",
                message.MessageId);

            telemetry.TrackException(ex);
            await receiver.AbandonMessageAsync(message, cancellationToken: cancellationToken);
        }
    }
    private async Task ProcessRtaWritebackMessageAsync(
        ServiceBusReceiver receiver,
        ServiceBusReceivedMessage message,
        JsonElement body,
        CancellationToken cancellationToken)
    {
        var envelope = JsonSerializer.Deserialize<RtaWritebackRetryEnvelope>(body.GetRawText());
        if (envelope is null || string.IsNullOrWhiteSpace(envelope.CorrelationId) || string.IsNullOrWhiteSpace(envelope.WritebackPayload))
        {
            await receiver.DeadLetterMessageAsync(message, "InvalidRtaWritebackRetry", "RTA writeback retry message is missing required fields.", cancellationToken);
            return;
        }

        if (DateTimeOffset.UtcNow >= envelope.RetryUntilUtc)
        {
            logger.LogWarning(
                "RTA writeback retry is past its initial retry window and will continue until RTA accepts it. CorrelationId={CorrelationId} RetryAttempt={RetryAttempt}",
                envelope.CorrelationId,
                envelope.RetryAttempt);
        }

        if (!_options.RtaStatusWritebackEnabled)
        {
            await RecordPollBatchWritebackAsync(envelope, false, null, "RtaStatusWritebackDisabled");
            await receiver.DeadLetterMessageAsync(message, "RtaWritebackDisabled", "RTA status writeback is disabled in this Service Bus role configuration.", cancellationToken);
            return;
        }

        try
        {
            using var payload = JsonDocument.Parse(envelope.WritebackPayload);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Invalid stored RTA writeback JSON. CorrelationId={CorrelationId}", envelope.CorrelationId);
            await receiver.DeadLetterMessageAsync(message, "InvalidRtaWritebackPayload", "Stored RTA writeback body is invalid JSON.", cancellationToken);
            return;
        }

        logger.LogInformation(
            "Sending stored RTA writeback directly to RTA. CorrelationId={CorrelationId} RetryAttempt={RetryAttempt}. APIM/DOCMP will not be called.",
            envelope.CorrelationId,
            envelope.RetryAttempt);

        try
        {
            await _concurrencyGates.RtaWriteback.WaitAsync(cancellationToken);
            RtaWritebackResult result;
            try
            {
                result = await appApiClient.WriteStatusAsync(envelope.CorrelationId, envelope.WritebackPayload, cancellationToken);
            }
            finally
            {
                _concurrencyGates.RtaWriteback.Release();
            }
            if (result.Succeeded == true)
            {
                await RecordPollBatchWritebackAsync(envelope, true, result.HttpStatusCode, "WritebackCompleted");
                await receiver.CompleteMessageAsync(message, cancellationToken);
                telemetry.TrackEvent("RtaWritebackRetryCompleted", new Dictionary<string, string>
                {
                    ["uniqueId"] = envelope.UniqueId,
                    ["correlationId"] = envelope.CorrelationId,
                    ["attempts"] = result.Attempts.ToString()
                });
                return;
            }

            if (result.HttpStatusCode is int statusCode &&
                statusCode >= 400 &&
                statusCode < 500 &&
                statusCode != 408 &&
                statusCode != 429)
            {
                await RecordPollBatchWritebackAsync(envelope, false, statusCode, "RtaWritebackRejected");
                await receiver.DeadLetterMessageAsync(
                    message,
                    "RtaWritebackRejected",
                    $"RTA permanently rejected the status writeback with HTTP {statusCode}.",
                    cancellationToken);
                await NotifyWritebackFailureAsync(
                    envelope,
                    "RTA_WRITEBACK_REJECTED",
                    $"RTA rejected the final status writeback with HTTP {statusCode}; the payload is retained in the dead-letter queue for support review.",
                    null,
                    cancellationToken);
                logger.LogCritical(
                    "RTA rejected final status writeback with a non-retryable HTTP response. CorrelationId={CorrelationId} StatusCode={StatusCode}",
                    envelope.CorrelationId,
                    statusCode);
                return;
            }

            await RecordPollBatchWritebackAsync(envelope, null, result.HttpStatusCode, "RtaWritebackRetrying");
            await ScheduleOrDeadLetterAsync(receiver, message, envelope, cancellationToken, result.HttpStatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Direct RTA writeback attempt threw. CorrelationId={CorrelationId}", envelope.CorrelationId);
            telemetry.TrackException(ex);
            await RecordPollBatchWritebackAsync(envelope, null, null, "RtaWritebackRetrying");
            await ScheduleOrDeadLetterAsync(receiver, message, envelope, cancellationToken);
        }
    }

    private async Task ScheduleOrDeadLetterAsync(
        ServiceBusReceiver receiver,
        ServiceBusReceivedMessage message,
        RtaWritebackRetryEnvelope envelope,
        CancellationToken cancellationToken,
        int? lastHttpStatusCode = null)
    {
        // Keep retrying transient RTA failures beyond the APIM retry window. The final status is not complete until RTA accepts it.
        var delaySeconds = Math.Min(300, 5 * Math.Pow(2, Math.Clamp(envelope.RetryAttempt, 0, 10)));
        var delay = TimeSpan.FromSeconds(delaySeconds);

        try
        {
            await retryQueue.ScheduleRtaWritebackRetryAsync(envelope, delay, cancellationToken);
            await receiver.CompleteMessageAsync(message, cancellationToken);
            logger.LogWarning(
                "Scheduled another direct RTA writeback attempt. CorrelationId={CorrelationId} RetryAttempt={RetryAttempt} DelaySeconds={DelaySeconds}",
                envelope.CorrelationId,
                envelope.RetryAttempt + 1,
                delay.TotalSeconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Could not schedule RTA writeback retry; moving message to DLQ. CorrelationId={CorrelationId}", envelope.CorrelationId);
            telemetry.TrackException(ex);
            await RecordPollBatchWritebackAsync(envelope, false, lastHttpStatusCode, "RtaWritebackRetryScheduleFailed");
            await receiver.DeadLetterMessageAsync(
                message,
                "RtaWritebackRetryScheduleFailed",
                "The next direct RTA writeback retry could not be scheduled.",
                cancellationToken);
            await NotifyWritebackFailureAsync(
                envelope,
                "SERVICE_BUS_RETRY_SCHEDULE_FAILED",
                "Project Pulse could not schedule the next direct RTA status writeback retry.",
                ex,
                cancellationToken);
        }
    }

    private async Task NotifyWritebackFailureAsync(
        RtaWritebackRetryEnvelope envelope,
        string failureType,
        string message,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        using var itemDocument = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            REQUEST_ID = envelope.RequestId,
            CORRELATION_ID = envelope.CorrelationId,
            IDEMPOTENCY_KEY = envelope.IdempotencyKey
        }));
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CORRELATION_ID"] = envelope.CorrelationId,
            ["REQUEST_ID"] = envelope.RequestId ?? string.Empty,
            ["RETRY_ATTEMPT"] = envelope.RetryAttempt.ToString(),
            ["RETRY_UNTIL_UTC"] = envelope.RetryUntilUtc.ToString("O")
        };
        if (exception is not null)
        {
            attributes["SERVICE_BUS_FAILURE_TYPE"] = exception.GetType().Name;
            attributes["SERVICE_BUS_FAILURE_MESSAGE"] = exception.Message;
        }

        await rtaNotificationClient.SendFailureAsync(
            itemDocument.RootElement,
            exception is null ? "RTA" : "SERVICE_BUS",
            failureType,
            message + (exception is null ? string.Empty : $" Error: {exception.GetType().Name}: {exception.Message}"),
            attributes,
            cancellationToken);
    }

    private async Task RecordPollBatchWritebackAsync(
        RtaWritebackRetryEnvelope envelope,
        bool? succeeded,
        int? httpStatusCode,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(envelope.PollRunId) || envelope.PollRecordOrdinal is not { } ordinal)
        {
            return;
        }

        try
        {
            await pollBatchLedger.RecordRtaWritebackAsync(
                envelope.PollRunId,
                ordinal,
                succeeded,
                httpStatusCode,
                reason,
                DateTimeOffset.UtcNow,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Could not update poll batch {PollRunId} record {Ordinal} with RTA writeback retry outcome.",
                envelope.PollRunId,
                ordinal);
        }
    }

}
