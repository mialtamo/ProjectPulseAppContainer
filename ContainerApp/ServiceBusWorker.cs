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
    IAppApiClient appApiClient,
    IRetryQueue retryQueue,
    IOptions<ClaimProcessorOptions> options,
    TelemetryClient telemetry,
    ILogger<ServiceBusWorker> logger) : BackgroundService
{
    private readonly ClaimProcessorOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var receiver = serviceBusClient.CreateReceiver(
            _options.ServiceBusQueueName,
            new ServiceBusReceiverOptions
            {
                ReceiveMode = ServiceBusReceiveMode.PeekLock,
                PrefetchCount = 0
            });

        logger.LogInformation(
            "Project Pulse Service Bus worker started. Queue={Queue}",
            _options.ServiceBusQueueName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var rtaHealth = await healthGate.CheckRtaAsync(stoppingToken);

                if (!rtaHealth.IsHealthy)
                {
                    logger.LogWarning(
                        "Service Bus processing paused because RTA is unhealthy. RtaHealthy={RtaHealthy}",
                        rtaHealth.IsHealthy);

                    await Task.Delay(
                        TimeSpan.FromSeconds(_options.GetServiceBusDependencyRetrySeconds()),
                        stoppingToken);

                    continue;
                }

                var messages = await receiver.ReceiveMessagesAsync(
                    _options.GetServiceBusReceiveBatchSize(),
                    TimeSpan.FromSeconds(_options.GetServiceBusReceiveWaitSeconds()),
                    stoppingToken);

                if (messages.Count == 0)
                {
                    continue;
                }

                logger.LogInformation(
                    "Received {Count} Service Bus retry message(s)",
                    messages.Count);

                foreach (var message in messages)
                {
                    await ProcessMessageAsync(receiver, message, stoppingToken);
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

        logger.LogInformation("Project Pulse Service Bus worker stopped");
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

            var results = await claimProcessor.ProcessBatchAsync([claim], [], cancellationToken);
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
            await receiver.DeadLetterMessageAsync(
                message,
                "RtaWritebackRetryWindowExpired",
                "The direct RTA writeback retry message reached its configured lifetime before processing.",
                cancellationToken);
            telemetry.TrackEvent("RtaWritebackRetryExpired", new Dictionary<string, string>
            {
                ["uniqueId"] = envelope.UniqueId,
                ["correlationId"] = envelope.CorrelationId
            });
            return;
        }

        if (!_options.RtaStatusWritebackEnabled)
        {
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
            var result = await appApiClient.WriteStatusAsync(envelope.CorrelationId, envelope.WritebackPayload, cancellationToken);
            if (result.Succeeded == true)
            {
                await receiver.CompleteMessageAsync(message, cancellationToken);
                telemetry.TrackEvent("RtaWritebackRetryCompleted", new Dictionary<string, string>
                {
                    ["uniqueId"] = envelope.UniqueId,
                    ["correlationId"] = envelope.CorrelationId,
                    ["attempts"] = result.Attempts.ToString()
                });
                return;
            }

            await ScheduleOrDeadLetterAsync(receiver, message, envelope, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Direct RTA writeback attempt threw. CorrelationId={CorrelationId}", envelope.CorrelationId);
            telemetry.TrackException(ex);
            await ScheduleOrDeadLetterAsync(receiver, message, envelope, cancellationToken);
        }
    }

    private async Task ScheduleOrDeadLetterAsync(
        ServiceBusReceiver receiver,
        ServiceBusReceivedMessage message,
        RtaWritebackRetryEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var remaining = envelope.RetryUntilUtc - DateTimeOffset.UtcNow;
        var delaySeconds = Math.Min(60, 5 * Math.Pow(2, Math.Clamp(envelope.RetryAttempt, 0, 10)));
        var delay = TimeSpan.FromSeconds(delaySeconds);
        if (remaining <= delay)
        {
            await receiver.DeadLetterMessageAsync(
                message,
                "RtaWritebackRetryWindowExpired",
                "RTA writeback did not succeed within the configured message lifetime.",
                cancellationToken);
            telemetry.TrackEvent("RtaWritebackRetryExpired", new Dictionary<string, string>
            {
                ["uniqueId"] = envelope.UniqueId,
                ["correlationId"] = envelope.CorrelationId
            });
            logger.LogError("RTA writeback retry window expired; moved to DLQ. CorrelationId={CorrelationId}", envelope.CorrelationId);
            return;
        }

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
            await receiver.DeadLetterMessageAsync(
                message,
                "RtaWritebackRetryScheduleFailed",
                "The next direct RTA writeback retry could not be scheduled.",
                cancellationToken);
        }
    }

}
