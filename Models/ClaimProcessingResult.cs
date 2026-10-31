namespace ProjectPulse.Processor.Models;

public enum ClaimProcessingDisposition
{
    Completed,
    QueuedForRetry,
    QueuedForWritebackRetry,
    Alerted,
    FailedWriteback,
    Skipped
}

public sealed record ClaimProcessingResult(
    string UniqueId,
    ClaimProcessingDisposition Disposition,
    string Reason,
    int? HttpStatusCode = null,
    string? BusinessCode = null);
