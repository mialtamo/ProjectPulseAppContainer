using ProjectPulse.Processor.Models;

namespace ProjectPulse.Processor.Services;

public interface IPollBatchLedger
{
    Task RecordReceivedAsync(
        string pollRunId,
        string source,
        DateTimeOffset receivedAtUtc,
        long? rtaPullDurationMs,
        bool rtaWritebackEnabled,
        IReadOnlyList<PollBatchRecordSeed> records,
        CancellationToken cancellationToken);

    Task RecordApimAttemptAsync(
        string pollRunId,
        int ordinal,
        DateTimeOffset attemptedAtUtc,
        CancellationToken cancellationToken);

    Task RecordApimResponseAsync(
        string pollRunId,
        int ordinal,
        int httpStatusCode,
        DateTimeOffset responseAtUtc,
        CancellationToken cancellationToken);

    Task RecordServiceBusBufferedAsync(
        string pollRunId,
        int ordinal,
        string reason,
        DateTimeOffset bufferedAtUtc,
        CancellationToken cancellationToken);

    Task RecordRtaWritebackAsync(
        string pollRunId,
        int ordinal,
        bool? succeeded,
        int? httpStatusCode,
        string? reason,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken);

    Task CompletePollAsync(
        string pollRunId,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PollBatchSummary>> ListAsync(
        int limit,
        DateTimeOffset? receivedAfterUtc,
        CancellationToken cancellationToken);

    Task<PollBatchDetail?> GetAsync(
        string pollRunId,
        CancellationToken cancellationToken);

    Task<int> DeleteForCleanupAsync(
        DateTimeOffset? receivedAfterUtc,
        CancellationToken cancellationToken);
}
