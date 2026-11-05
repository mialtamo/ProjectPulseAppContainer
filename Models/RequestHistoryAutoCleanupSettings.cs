namespace ProjectPulse.Processor.Models;

public sealed record RequestHistoryAutoCleanupSettings(
    bool Enabled,
    int RetentionMinutes,
    int CheckIntervalMinutes,
    DateTimeOffset? LastRunAtUtc);

public sealed record RequestHistoryAutoCleanupUpdate(
    bool Enabled,
    int RetentionMinutes,
    int CheckIntervalMinutes);
