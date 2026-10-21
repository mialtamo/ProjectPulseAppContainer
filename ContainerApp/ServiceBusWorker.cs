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
                var (rtaHealth, apimHealth) = await healthGate.CheckAsync(stoppingToken);

                if (!rtaHealth.IsHealthy || !apimHealth.IsHealthy)
                {
                    logger.LogWarning(
                        "Service Bus processing paused because dependencies are unhealthy. RtaHealthy={RtaHealthy} ApimHealthy={ApimHealthy}",
                        rtaHealth.IsHealthy,
                        apimHealth.IsHealthy);

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
                case ClaimProcessingDisposition.QueuedForRetry:
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
}

