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
        var message = new ServiceBusMessage(payload)
        {
            ContentType = "application/json",
            MessageId = envelope.UniqueId,
            CorrelationId = envelope.UniqueId,
            TimeToLive = TimeSpan.FromMinutes(_options.ServiceBusMessageTtlMinutes),
            Subject = "ClaimRetry"
        };

        message.ApplicationProperties["reason"] = envelope.Reason;
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
            await _sender.SendMessageAsync(message, cancellationToken);
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
            ["ttlMinutes"] = _options.ServiceBusMessageTtlMinutes.ToString()
        });

        _logger.LogWarning(
            "Claim {UniqueId} buffered to Service Bus. Reason={Reason}",
            envelope.UniqueId,
            envelope.Reason);
    }

    public async ValueTask DisposeAsync() => await _sender.DisposeAsync();
}
