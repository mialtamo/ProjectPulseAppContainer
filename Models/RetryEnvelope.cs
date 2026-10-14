namespace ProjectPulse.Processor.Models;

public sealed record RetryEnvelope(
    string UniqueId,
    string? RequestId,
    string? IdempotencyKey,
    string Reason,
    DateTimeOffset QueuedAtUtc,
    string OriginalPayload,
    string TransformedPayload);
