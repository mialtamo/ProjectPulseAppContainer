using ProjectPulse.Processor.Models;

namespace ProjectPulse.Processor.Services;

public interface IRequestHistoryStore
{
    Task RecordApimAttemptAsync(
        string batchId,
        IReadOnlyList<string> uniqueIds,
        IReadOnlyList<string> rtaResponsePayloads,
        string requestPayload,
        DateTimeOffset? pipelineStartedAtUtc,
        long? rtaPullDurationMs,
        long translationDurationMs,
        DateTimeOffset sentAtUtc,
        CancellationToken cancellationToken);

    Task RecordApimResultAsync(
        string batchId,
        IReadOnlyList<string> uniqueIds,
        string status,
        int? httpStatusCode,
        string? businessResponseCode,
        string? responsePayload,
        string? reason,
        bool sentToServiceBus,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken,
        DateTimeOffset? apimResponseAtUtc = null,
        long? apimRoundTripDurationMs = null);

    Task RecordWritebackTimingAsync(
        string batchId,
        IReadOnlyList<string> uniqueIds,
        long writebackDurationMs,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken);

    Task RecordRtaStatusPayloadAsync(
        string batchId,
        IReadOnlyList<string> uniqueIds,
        IReadOnlyList<string> payloads,
        CancellationToken cancellationToken);

    Task RecordServiceBusAsync(
        string batchId,
        IReadOnlyList<string> uniqueIds,
        IReadOnlyList<string> rtaResponsePayloads,
        string reason,
        DateTimeOffset bufferedAtUtc,
        CancellationToken cancellationToken);

    Task<RequestHistoryPage> ListAsync(
        int limit,
        string? continuationToken,
        CancellationToken cancellationToken);

    Task<RequestHistoryTimingAverages> GetTimingAveragesAsync(CancellationToken cancellationToken);

    Task<RequestHistoryItem?> GetAsync(
        string recordId,
        CancellationToken cancellationToken);
}

