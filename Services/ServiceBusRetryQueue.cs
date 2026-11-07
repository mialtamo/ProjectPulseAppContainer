using System.Text.Json;
using Azure.Messaging.ServiceBus;
using ProjectPulse.Processor.Models;
using ProjectPulse.Processor.Options;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProjectPulse.Processor.Services;

public sealed class ServiceBusRetryQueue : IRetryQueue, IAsyncDisposable
{
    private readonly ServiceBusSender _sender;
    private readonly ClaimProcessorOptions _options;
    private readonly TelemetryClient _telemetry;
    private readonly ILogger<ServiceBusRetryQueue> _logger;

    public ServiceBusRetryQueue(
        ServiceBusClient client,
        IOptions<ClaimProcessorOptions> options,
        TelemetryClient telemetry,
        ILogger<ServiceBusRetryQueue> logger)
    {
        _options = options.Value;
        _telemetry = telemetry;
        _logger = logger;
        _sender = client.CreateSender(_options.ServiceBusQueueName);
    }

    public async Task EnqueueAsync(RetryEnvelope envelope, CancellationToken cancellationToken)
    {
        var retryUntilUtc = envelope.RetryUntilUtc
            ?? envelope.QueuedAtUtc.AddMinutes(_options.ServiceBusMessageTtlMinutes);
        var finalizeAtUtc = retryUntilUtc.AddSeconds(-_options.GetServiceBusFinalizationReserveSeconds());
        var nowUtc = DateTimeOffset.UtcNow;
        var remaining = retryUntilUtc - nowUtc;
        if (remaining <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"Service Bus retry deadline has elapsed for claim {envelope.UniqueId}.");
        }

        var retryDelaySeconds = Math.Min(60, 5 * Math.Pow(2, Math.Clamp(envelope.RetryAttempt - 1, 0, 10)));
        var retryDelay = TimeSpan.FromSeconds(retryDelaySeconds);
        var scheduledAtUtc = nowUtc.Add(retryDelay);
        if (scheduledAtUtc > finalizeAtUtc)
        {
            scheduledAtUtc = finalizeAtUtc;
        }
        var scheduled = envelope.RetryAttempt > 0 && scheduledAtUtc > nowUtc;
        var ttl = scheduled ? retryUntilUtc - scheduledAtUtc : remaining;
        var message = new ServiceBusMessage(JsonSerializer.Serialize(envelope))
        {
            ContentType = "application/json",
            MessageId = envelope.UniqueId + "-apim-" + envelope.RetryAttempt + "-" + Guid.NewGuid().ToString("N"),
            CorrelationId = envelope.UniqueId,
            TimeToLive = ttl < TimeSpan.FromMinutes(_options.ServiceBusMessageTtlMinutes)
                ? ttl
                : TimeSpan.FromMinutes(_options.ServiceBusMessageTtlMinutes),
            Subject = "ClaimRetry"
        };

        message.ApplicationProperties["reason"] = envelope.Reason;
        message.ApplicationProperties["originalQueuedAtUtc"] = envelope.QueuedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(envelope.RequestId))
        {
            message.ApplicationProperties["requestId"] = envelope.RequestId;
        }

        if (!string.IsNullOrWhiteSpace(envelope.IdempotencyKey))
        {
            message.ApplicationProperties["idempotencyKey"] = envelope.IdempotencyKey;
        }

        try
        {
            if (scheduled)
            {
                // Scheduled TTL starts when the message becomes active, so set it to the time remaining after activation.
                // This keeps the broker expiry at the original per-record deadline.
                await _sender.ScheduleMessageAsync(message, scheduledAtUtc, cancellationToken);
            }
            else
            {
                // During the reserved finalization interval, deliver immediately for the final RTA status attempt.
                await _sender.SendMessageAsync(message, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _telemetry.TrackException(ex, new Dictionary<string, string>
            {
                ["uniqueId"] = envelope.UniqueId,
                ["reason"] = envelope.Reason,
                ["queue"] = _options.ServiceBusQueueName
            });

            _logger.LogError(
                ex,
                "Service Bus buffering failed for claim {UniqueId}. Queue={Queue} Reason={Reason}",
                envelope.UniqueId,
                _options.ServiceBusQueueName,
                envelope.Reason);

            throw;
        }

        _telemetry.TrackEvent("ClaimQueuedForRetry", new Dictionary<string, string>
        {
            ["uniqueId"] = envelope.UniqueId,
            ["reason"] = envelope.Reason,
            ["retryAttempt"] = envelope.RetryAttempt.ToString(),
            ["retryUntilUtc"] = retryUntilUtc.ToString("O"),
            ["retryDelaySeconds"] = scheduled ? (scheduledAtUtc - nowUtc).TotalSeconds.ToString("0") : "0"
        });

        _logger.LogWarning(
            "Claim {UniqueId} buffered to Service Bus. Reason={Reason} RetryAttempt={RetryAttempt} RetryUntilUtc={RetryUntilUtc} RetryDelaySeconds={RetryDelaySeconds}",
            envelope.UniqueId,
            envelope.Reason,
            envelope.RetryAttempt,
            retryUntilUtc.ToString("O"),
            scheduled ? (scheduledAtUtc - nowUtc).TotalSeconds : 0);
    }

    public async Task EnqueueRtaWritebackAsync(
        RtaWritebackRetryEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var remaining = envelope.RetryUntilUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"RTA writeback deadline has elapsed for claim {envelope.UniqueId}.");
        }

        await _sender.SendMessageAsync(CreateRtaWritebackMessage(envelope, remaining), cancellationToken);

        _telemetry.TrackEvent("RtaWritebackQueuedForRetry", new Dictionary<string, string>
        {
            ["uniqueId"] = envelope.UniqueId,
            ["correlationId"] = envelope.CorrelationId,
            ["remainingSeconds"] = remaining.TotalSeconds.ToString("0")
        });
        _logger.LogWarning(
            "Stored RTA writeback payload queued for direct retry. CorrelationId={CorrelationId} RemainingSeconds={RemainingSeconds}",
            envelope.CorrelationId,
            remaining.TotalSeconds);
    }

    public async Task ScheduleRtaWritebackRetryAsync(
        RtaWritebackRetryEnvelope envelope,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var remaining = envelope.RetryUntilUtc - nowUtc;
        // Leave at least 15 seconds after a scheduled message activates for the worker to attempt the PUT.
        if (remaining <= TimeSpan.FromSeconds(15))
        {
            throw new InvalidOperationException($"RTA writeback deadline is too close for another retry for claim {envelope.UniqueId}.");
        }

        var next = envelope with { RetryAttempt = envelope.RetryAttempt + 1 };
        var latestUsefulDelay = remaining - TimeSpan.FromSeconds(15);
        var scheduledDelay = delay < latestUsefulDelay ? delay : TimeSpan.FromSeconds(1);
        var scheduledTime = nowUtc.Add(scheduledDelay);
        var actualScheduledDelay = scheduledTime - nowUtc;
        var message = CreateRtaWritebackMessage(next, remaining - actualScheduledDelay);
        await _sender.ScheduleMessageAsync(message, scheduledTime, cancellationToken);

        _telemetry.TrackEvent("RtaWritebackRetryScheduled", new Dictionary<string, string>
        {
            ["uniqueId"] = envelope.UniqueId,
            ["correlationId"] = envelope.CorrelationId,
            ["retryAttempt"] = next.RetryAttempt.ToString(),
            ["delaySeconds"] = delay.TotalSeconds.ToString("0")
        });
    }

    private static ServiceBusMessage CreateRtaWritebackMessage(
        RtaWritebackRetryEnvelope envelope,
        TimeSpan timeToLive)
    {
        var message = new ServiceBusMessage(JsonSerializer.Serialize(envelope))
        {
            ContentType = "application/json",
            MessageId = $"{envelope.UniqueId}-rta-writeback-{envelope.RetryAttempt}-{Guid.NewGuid():N}",
            CorrelationId = envelope.CorrelationId,
            TimeToLive = timeToLive,
            Subject = RtaWritebackRetryEnvelope.TypeName
        };

        message.ApplicationProperties["messageType"] = RtaWritebackRetryEnvelope.TypeName;
        message.ApplicationProperties["reason"] = envelope.Reason;
        message.ApplicationProperties["originalQueuedAtUtc"] = envelope.QueuedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        message.ApplicationProperties["correlationId"] = envelope.CorrelationId;
        message.ApplicationProperties["retryAttempt"] = envelope.RetryAttempt;
        if (!string.IsNullOrWhiteSpace(envelope.RequestId))
        {
            message.ApplicationProperties["requestId"] = envelope.RequestId;
        }
        if (!string.IsNullOrWhiteSpace(envelope.IdempotencyKey))
        {
            message.ApplicationProperties["idempotencyKey"] = envelope.IdempotencyKey;
        }

        return message;
    }

    public async ValueTask DisposeAsync() => await _sender.DisposeAsync();
}
