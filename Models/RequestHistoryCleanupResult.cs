namespace ProjectPulse.Processor.Models;

public sealed record RequestHistoryCleanupResult(
    int DeletedRecords,
    int DeletedPayloadBlobs,
    int PayloadCleanupFailures);
