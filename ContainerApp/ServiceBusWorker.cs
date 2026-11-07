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
        var mode = _concurrencyGates.Mode;
        var maxConcurrency = _concurrencyGates.MaxConcurrentMessages;
        var inFlight = new List<Task>();

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
                    if (inFlight.Count >= maxConcurrency)
                    {
                        continue;
                    }

                    // Max-delivery handling can move a claim to DLQ before its 10-minute TTL.
                    // Recover only while its original deadline is still live; never extend it.
                    var deadLetterMessage = await deadLetterReceiver.ReceiveMessageAsync(
                        TimeSpan.FromMilliseconds(50),
                        stoppingToken);
                    if (deadLetterMessage is not null)
                    {
                        inFlight.Add(ProcessDeadLetterMessageAsync(deadLetterReceiver, deadLetterMessage, stoppingToken));
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

    private async Task NotifyExpiredClaimFailureAsync(
        RetryEnvelope envelope,
        DateTimeOffset retryUntilUtc,
        CancellationToken cancellationToken)
    {
        using var itemDocument = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            REQUEST_ID = envelope.RequestId,
            CORRELATION_ID = envelope.UniqueId,
            IDEMPOTENCY_KEY = envelope.IdempotencyKey
        }));
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CORRELATION_ID"] = envelope.UniqueId,
            ["REQUEST_ID"] = envelope.RequestId ?? string.Empty,
            ["RETRY_ATTEMPT"] = envelope.RetryAttempt.ToString(),
            ["RETRY_UNTIL_UTC"] = retryUntilUtc.ToString("O")
        };

        try
        {
            await rtaNotificationClient.SendFailureAsync(
                itemDocument.RootElement,
                "SERVICEBUS",
                "APIM_RETRY_DEADLINE_MISSED",
                "The Service Bus record reached its hard 10-minute deadline before Project Pulse could send the final status to RTA.",
                attributes,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not send the deadline-missed notification for claim {UniqueId}.", envelope.UniqueId);
            telemetry.TrackException(ex);
        }
    }

    private async Task ProcessDeadLetterMessageAsync(
        ServiceBusReceiver deadLetterReceiver,
        ServiceBusReceivedMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(message.Body.ToString());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                await deadLetterReceiver.CompleteMessageAsync(message, cancellationToken);
                return;
            }

            if (root.TryGetProperty("MessageType", out var messageType) &&
                messageType.ValueKind == JsonValueKind.String &&
                string.Equals(messageType.GetString(), RtaWritebackRetryEnvelope.TypeName, StringComparison.OrdinalIgnoreCase))
            {
                var writeback = JsonSerializer.Deserialize<RtaWritebackRetryEnvelope>(root.GetRawText());
                if (writeback is null)
                {
                    await deadLetterReceiver.CompleteMessageAsync(message, cancellationToken);
                    return;
                }

                if (DateTimeOffset.UtcNow >= writeback.RetryUntilUtc)
                {
                    await NotifyWritebackFailureAsync(
                        writeback,
                        "RTA_WRITEBACK_WINDOW_EXPIRED",
                        "A dead-lettered RTA status retry reached its original 10-minute deadline without acceptance.",
                        null,
                        cancellationToken);
                    await deadLetterReceiver.CompleteMessageAsync(message, cancellationToken);
                    return;
                }

                await retryQueue.EnqueueRtaWritebackAsync(writeback, cancellationToken);
                await deadLetterReceiver.CompleteMessageAsync(message, cancellationToken);
                telemetry.TrackEvent("ServiceBusWritebackRecoveredFromDeadLetter", new Dictionary<string, string>
                {
                    ["correlationId"] = writeback.CorrelationId,
                    ["retryUntilUtc"] = writeback.RetryUntilUtc.ToString("O")
                });
                return;
            }

            var envelope = JsonSerializer.Deserialize<RetryEnvelope>(root.GetRawText());
            if (envelope is null || string.IsNullOrWhiteSpace(envelope.OriginalPayload))
            {
                await deadLetterReceiver.CompleteMessageAsync(message, cancellationToken);
                return;
            }

            var retryUntilUtc = envelope.RetryUntilUtc
                ?? envelope.QueuedAtUtc.AddMinutes(_options.ServiceBusMessageTtlMinutes);
            envelope = envelope with { RetryUntilUtc = retryUntilUtc };
            var nowUtc = DateTimeOffset.UtcNow;
            if (nowUtc >= retryUntilUtc)
            {
                logger.LogCritical(
                    "Dead-lettered claim {UniqueId} passed its original 10-minute deadline without a final RTA status. Removing expired payload.",
                    envelope.UniqueId);
                telemetry.TrackEvent("ServiceBusDeadLetterExpiredBeforeFinalization", new Dictionary<string, string>
                {
                    ["uniqueId"] = envelope.UniqueId,
                    ["retryUntilUtc"] = retryUntilUtc.ToString("O")
                });
                await NotifyExpiredClaimFailureAsync(envelope, retryUntilUtc, cancellationToken);
                await deadLetterReceiver.CompleteMessageAsync(message, cancellationToken);
                return;
            }

            if (nowUtc >= retryUntilUtc.AddSeconds(-_options.GetServiceBusFinalizationReserveSeconds()))
            {
                using var claimDocument = JsonDocument.Parse(envelope.OriginalPayload);
                var claim = claimDocument.RootElement.Clone();
                await claimProcessor.ProcessBatchAsync(
                    [claim],
                    [],
                    cancellationToken,
                    retryEnvelope: envelope);
            }
            else
            {
                await retryQueue.EnqueueAsync(
                    envelope with
                    {
                        Reason = "RecoveredMaxDeliveryRetry",
                        RetryAttempt = envelope.RetryAttempt + 1
                    },
                    cancellationToken);
            }

            await deadLetterReceiver.CompleteMessageAsync(message, cancellationToken);
            telemetry.TrackEvent("ServiceBusClaimRecoveredFromDeadLetter", new Dictionary<string, string>
            {
                ["uniqueId"] = envelope.UniqueId,
                ["retryUntilUtc"] = retryUntilUtc.ToString("O")
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Invalid dead-letter message JSON. MessageId={MessageId}", message.MessageId);
            telemetry.TrackException(ex);
            await deadLetterReceiver.CompleteMessageAsync(message, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not recover a dead-letter message within its original deadline. MessageId={MessageId}", message.MessageId);
            telemetry.TrackException(ex);
            await deadLetterReceiver.AbandonMessageAsync(message, cancellationToken: cancellationToken);
        }
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
            await receiver.CompleteMessageAsync(message, cancellationToken);
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

            await receiver.CompleteMessageAsync(message, cancellationToken);

            return;
        }

        if (envelope is null || string.IsNullOrWhiteSpace(envelope.OriginalPayload))
        {
            logger.LogError("Service Bus retry envelope is missing OriginalPayload. MessageId={MessageId}", message.MessageId);

            await receiver.CompleteMessageAsync(message, cancellationToken);

            return;
        }

        // The original enqueue time starts the hard record lifetime. Never reset it on pickup or requeue.
        envelope = envelope.RetryUntilUtc is null
            ? envelope with { RetryUntilUtc = envelope.QueuedAtUtc.AddMinutes(_options.ServiceBusMessageTtlMinutes) }
            : envelope;

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
                    await receiver.CompleteMessageAsync(message, cancellationToken);
                    logger.LogCritical(
                        "Completed claim retry {UniqueId} after its failed RTA writeback could not be buffered. APIM will not be replayed. Reason={Reason}",
                        envelope.UniqueId,
                        result.Reason);
                    break;

                case ClaimProcessingDisposition.Skipped:
                    await RequeueClaimOrFinalizeAsync(
                        receiver, message, envelope, "ServiceBusProcessingSkipped", cancellationToken);
                    logger.LogWarning(
                        "Service Bus claim {UniqueId} was rescheduled within its original deadline. Disposition={Disposition} Reason={Reason}.",
                        envelope.UniqueId,
                        result.Disposition,
                        result.Reason);
                    break;

                default:
                    await RequeueClaimOrFinalizeAsync(
                        receiver, message, envelope, "ServiceBusProcessingUnresolved", cancellationToken);
                    break;
            }
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "OriginalPayload is invalid JSON for Service Bus message {MessageId}", message.MessageId);
            telemetry.TrackException(ex);

            await receiver.CompleteMessageAsync(message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Service Bus retry processing failed for message {MessageId}. Rescheduling within the original record deadline.",
                message.MessageId);

            telemetry.TrackException(ex);
            await RequeueClaimOrFinalizeAsync(
                receiver, message, envelope, "ServiceBusProcessingFailure", cancellationToken);
        }
    }
    private async Task RequeueClaimOrFinalizeAsync(
        ServiceBusReceiver receiver,
        ServiceBusReceivedMessage message,
        RetryEnvelope envelope,
        string reason,
        CancellationToken cancellationToken)
    {
        var retryUntilUtc = envelope.RetryUntilUtc
            ?? envelope.QueuedAtUtc.AddMinutes(_options.ServiceBusMessageTtlMinutes);

        if (DateTimeOffset.UtcNow >= retryUntilUtc)
        {
            try
            {
                using var document = JsonDocument.Parse(envelope.OriginalPayload);
                var claim = document.RootElement.Clone();
                await claimProcessor.ProcessBatchAsync(
                    [claim],
                    [],
                    cancellationToken,
                    retryEnvelope: envelope with { RetryUntilUtc = retryUntilUtc });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogCritical(
                    ex,
                    "Could not send the final RTA status after the Service Bus deadline for claim {UniqueId}.",
                    envelope.UniqueId);
                telemetry.TrackException(ex);
            }

            await receiver.CompleteMessageAsync(message, cancellationToken);
            return;
        }

        try
        {
            await retryQueue.EnqueueAsync(
                envelope with
                {
                    Reason = reason,
                    RetryUntilUtc = retryUntilUtc,
                    RetryAttempt = envelope.RetryAttempt + 1
                },
                cancellationToken);
            await receiver.CompleteMessageAsync(message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Could not reschedule claim {UniqueId} before its original Service Bus deadline. Keeping the current delivery for broker retry.",
                envelope.UniqueId);
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
            await receiver.CompleteMessageAsync(message, cancellationToken);
            return;
        }

        if (DateTimeOffset.UtcNow >= envelope.RetryUntilUtc)
        {
            await RecordPollBatchWritebackAsync(envelope, false, null, "RtaWritebackRetryWindowExpired");
            await NotifyWritebackFailureAsync(
                envelope,
                "RTA_WRITEBACK_WINDOW_EXPIRED",
                "The final RTA status writeback was not accepted before the record's 10-minute Service Bus lifetime expired.",
                null,
                cancellationToken);
            await receiver.CompleteMessageAsync(message, cancellationToken);
            logger.LogCritical(
                "RTA writeback window expired; the payload is being removed from Service Bus. CorrelationId={CorrelationId} RetryAttempt={RetryAttempt}",
                envelope.CorrelationId,
                envelope.RetryAttempt);
            return;
        }

        if (!_options.RtaStatusWritebackEnabled)
        {
            await RecordPollBatchWritebackAsync(envelope, false, null, "RtaStatusWritebackDisabled");
            await receiver.CompleteMessageAsync(message, cancellationToken);
            return;
        }

        try
        {
            using var payload = JsonDocument.Parse(envelope.WritebackPayload);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Invalid stored RTA writeback JSON. CorrelationId={CorrelationId}", envelope.CorrelationId);
            await receiver.CompleteMessageAsync(message, cancellationToken);
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
                await NotifyWritebackFailureAsync(
                    envelope,
                    "RTA_WRITEBACK_REJECTED",
                    $"RTA rejected the final status writeback with HTTP {statusCode}. The message was removed from Service Bus.",
                    null,
                    cancellationToken);
                await receiver.CompleteMessageAsync(message, cancellationToken);
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
            await NotifyWritebackFailureAsync(
                envelope,
                "SERVICE_BUS_RETRY_SCHEDULE_FAILED",
                "Project Pulse could not schedule another direct RTA status writeback attempt before the hard 10-minute deadline. The message was removed from Service Bus.",
                ex,
                cancellationToken);
            await receiver.CompleteMessageAsync(message, cancellationToken);
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
