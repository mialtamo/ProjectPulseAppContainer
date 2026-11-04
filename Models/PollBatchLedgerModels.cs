namespace ProjectPulse.Processor.Models;

public sealed record PollBatchRecordSeed(
    int Ordinal,
    string UniqueId,
    string? RtaRequestId);

public sealed record PollBatchRecord(
    int Ordinal,
    string UniqueId,
    string? RtaRequestId,
    DateTimeOffset ReceivedAtUtc,
    DateTimeOffset? ApimAttemptedAtUtc,
    DateTimeOffset? ApimResponseAtUtc,
    int? ApimHttpStatusCode,
    DateTimeOffset? ServiceBusBufferedAtUtc,
    string? ServiceBusReason,
    DateTimeOffset? RtaWritebackAtUtc,
    bool? RtaWritebackSucceeded,
    int? RtaWritebackHttpStatusCode,
    string? RtaWritebackReason,
    string? ProcessingDisposition,
    DateTimeOffset? ProcessingCompletedAtUtc);

public sealed record PollBatchSummary(
    string PollRunId,
    string Source,
    DateTimeOffset ReceivedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long? RtaPullDurationMs,
    int ReceivedCount,
    int ApimAttemptCount,
    int ApimResponseCount,
    int ServiceBusBufferedCount,
    int RtaWritebackSuccessCount,
    int RtaWritebackFailureCount,
    int PendingCount);

public sealed record PollBatchDetail(
    PollBatchSummary Summary,
    IReadOnlyList<PollBatchRecord> Records);
