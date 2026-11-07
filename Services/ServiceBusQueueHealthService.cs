using System.Globalization;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Options;
using ProjectPulse.Processor.Options;

namespace ProjectPulse.Processor.Services;

public sealed class ServiceBusQueueHealthService
{
    private const int AgeSampleLimit = 100;

    private readonly ServiceBusAdministrationClient _administrationClient;
    private readonly ServiceBusClient _serviceBusClient;
    private readonly ClaimProcessorOptions _options;

    public ServiceBusQueueHealthService(
        ServiceBusAdministrationClient administrationClient,
        ServiceBusClient serviceBusClient,
        IOptions<ClaimProcessorOptions> options)
    {
        _administrationClient = administrationClient;
        _serviceBusClient = serviceBusClient;
        _options = options.Value;
    }

    public async Task<ServiceBusQueueHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var queueName = _options.ServiceBusQueueName;
        var runtime = (await _administrationClient.GetQueueRuntimePropertiesAsync(
            queueName,
            cancellationToken)).Value;

        // Peek is non-destructive: this does not receive, lock, or complete any messages.
        await using var receiver = _serviceBusClient.CreateReceiver(queueName);
        var messages = await receiver.PeekMessagesAsync(AgeSampleLimit, cancellationToken: cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var ages = messages.Select(message =>
        {
            var hasOriginalTime = TryGetOriginalQueueTime(message, out var queuedAtUtc);
            var ageSeconds = Math.Max(0, (now - queuedAtUtc).TotalSeconds);
            return new AgeSample(ageSeconds, hasOriginalTime);
        }).ToArray();

        return new ServiceBusQueueHealthSnapshot(
            queueName,
            runtime.ActiveMessageCount,
            runtime.ScheduledMessageCount,
            runtime.DeadLetterMessageCount,
            runtime.ActiveMessageCount + runtime.ScheduledMessageCount,
            ages.Length,
            ages.Count(sample => sample.HasOriginalQueueTime),
            ages.Length == 0 ? null : ages.Average(sample => sample.AgeSeconds),
            ages.Length == 0 ? null : ages.Max(sample => sample.AgeSeconds),
            ages.Length == 0 ? null : ages.Min(sample => sample.AgeSeconds),
            now);
    }

    private static bool TryGetOriginalQueueTime(
        ServiceBusReceivedMessage message,
        out DateTimeOffset queuedAtUtc)
    {
        if (message.ApplicationProperties.TryGetValue("originalQueuedAtUtc", out var value)
            && DateTimeOffset.TryParse(
                Convert.ToString(value, CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out queuedAtUtc))
        {
            return true;
        }

        // Messages created before originalQueuedAtUtc was added fall back to their current
        // Service Bus enqueue time. The page labels this as a sample rather than a full queue age.
        queuedAtUtc = message.EnqueuedTime;
        return false;
    }

    private sealed record AgeSample(double AgeSeconds, bool HasOriginalQueueTime);
}

public sealed record ServiceBusQueueHealthSnapshot(
    string QueueName,
    long ActiveMessageCount,
    long ScheduledMessageCount,
    long DeadLetterMessageCount,
    long PendingMessageCount,
    int AgeSampleCount,
    int OriginalQueueTimestampSampleCount,
    double? AverageAgeSeconds,
    double? OldestAgeSeconds,
    double? YoungestAgeSeconds,
    DateTimeOffset CheckedAtUtc);
