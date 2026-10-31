namespace ProjectPulse.Processor.Models;

public sealed record RequestHistoryCleanupProgress(
    string OperationId,
    string Range,
    string Status,
    string Phase,
    int ProgressPercent,
    int TotalItems,
    int ProcessedItems,
    int DeletedRecords,
    int DeletedPayloadBlobs,
    int PayloadCleanupFailures,
    string? Error,
    DateTimeOffset UpdatedAtUtc);
