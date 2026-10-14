using ProjectPulse.Processor.Models;

namespace ProjectPulse.Processor.Services;

public interface IRequestHistoryStore
{
    Task RecordApimAttemptAsync(
        string batchId,
        IReadOnlyList<string> uniqueIds,
        string requestPayload,
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
        CancellationToken cancellationToken);

    Task RecordServiceBusAsync(
        string batchId,
        IReadOnlyList<string> uniqueIds,
        string reason,
        DateTimeOffset bufferedAtUtc,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RequestHistoryItem>> ListAsync(
        int limit,
        CancellationToken cancellationToken);

    Task<RequestHistoryItem?> GetAsync(
        string recordId,
        CancellationToken cancellationToken);
}
