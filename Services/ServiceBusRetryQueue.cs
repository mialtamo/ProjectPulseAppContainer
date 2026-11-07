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
        var payload = JsonSerializer.Serialize(envelope);
        var retryUntilUtc = envelope.RetryUntilUtc
            ?? (envelope.RetryAttempt > 0
                ? envelope.QueuedAtUtc.AddMinutes(_options.ServiceBusMessageTtlMinutes)
                : (DateTimeOffset?)null);
        var nowUtc = DateTimeOffset.UtcNow;
        var remaining = retryUntilUtc is { } deadlineUtc
            ? deadlineUtc - nowUtc
            : TimeSpan.MaxValue;
        var message = new ServiceBusMessage(payload)
        {
            ContentType = "application/json",
            MessageId = envelope.UniqueId + "-apim-" + envelope.RetryAttempt + "-" + Guid.NewGuid().ToString("N"),
            CorrelationId = envelope.UniqueId,
            // Keep the broker message past the logical retry deadline so the worker can send a final RTA outcome.
            TimeToLive = TimeSpan.FromMinutes(_options.GetServiceBusMessageRetentionMinutes()),
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

        var retryDelaySeconds = Math.Min(60, 5 * Math.Pow(2, Math.Clamp(envelope.RetryAttempt - 1, 0, 10)));
        var retryDelay = TimeSpan.FromSeconds(retryDelaySeconds);
        var scheduled = envelope.RetryAttempt > 0 && remaining > TimeSpan.Zero;
        var scheduledAtUtc = scheduled
            ? nowUtc.Add(remaining < retryDelay ? remaining : retryDelay)
            : nowUtc;

        try
        {
            if (scheduled)
            {
                // The last retry is scheduled at the logical deadline; when received, the worker finalizes it.
                await _sender.ScheduleMessageAsync(message, scheduledAtUtc, cancellationToken);
            }
            else
            {
                // If the deadline elapsed during processing, deliver immediately for finalization instead of dropping it.
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
            ["retryUntilUtc"] = retryUntilUtc?.ToString("O") ?? "starts-on-worker-pickup",
            ["retryDelaySeconds"] = scheduled ? (scheduledAtUtc - nowUtc).TotalSeconds.ToString("0") : "0"
        });

        _logger.LogWarning(
            "Claim {UniqueId} buffered to Service Bus. Reason={Reason} RetryAttempt={RetryAttempt} RetryUntilUtc={RetryUntilUtc} RetryDelaySeconds={RetryDelaySeconds}",
            envelope.UniqueId,
            envelope.Reason,
            envelope.RetryAttempt,
            retryUntilUtc?.ToString("O") ?? "starts-on-worker-pickup",
            scheduled ? (scheduledAtUtc - nowUtc).TotalSeconds : 0);
    }

    public async Task EnqueueRtaWritebackAsync(
        RtaWritebackRetryEnvelope envelope,
        CancellationToken cancellationToken)
    {
        await _sender.SendMessageAsync(CreateRtaWritebackMessage(envelope, TimeSpan.FromMinutes(_options.GetServiceBusMessageRetentionMinutes())), cancellationToken);

        _telemetry.TrackEvent("RtaWritebackQueuedForRetry", new Dictionary<string, string>
        {
            ["uniqueId"] = envelope.UniqueId,
            ["correlationId"] = envelope.CorrelationId,
            ["retentionMinutes"] = _options.GetServiceBusMessageRetentionMinutes().ToString()
        });
        _logger.LogWarning(
            "Stored RTA writeback payload queued for direct retry. CorrelationId={CorrelationId} BrokerRetentionMinutes={RetentionMinutes}",
            envelope.CorrelationId,
            _options.GetServiceBusMessageRetentionMinutes());
    }

    public async Task ScheduleRtaWritebackRetryAsync(
        RtaWritebackRetryEnvelope envelope,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        var next = envelope with { RetryAttempt = envelope.RetryAttempt + 1 };
        var message = CreateRtaWritebackMessage(
            next,
            TimeSpan.FromMinutes(_options.GetServiceBusMessageRetentionMinutes()));
        var scheduledTime = DateTimeOffset.UtcNow.Add(delay);
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
