namespace ProjectPulse.Processor.Models;

public sealed record RetryEnvelope(
    string UniqueId,
    string? RequestId,
    string? IdempotencyKey,
    string Reason,
    DateTimeOffset QueuedAtUtc,
    string OriginalPayload,
    string TransformedPayload)
{
    public DateTimeOffset? RetryUntilUtc { get; init; }
    public int RetryAttempt { get; init; }
    public int? LastHttpStatusCode { get; init; }
    public string? LastHttpReasonPhrase { get; init; }
    public string? LastHttpResponseBody { get; init; }
    public string? LastHttpResponseHeaders { get; init; }
    public long? LastResponseTimeMs { get; init; }
}

/// <summary>
/// Carries the exact translated RTA status PUT body after APIM/DOCMP has already responded.
/// Service Bus consumers use this message for RTA-only retries and never replay APIM/DOCMP.
/// </summary>
public sealed record RtaWritebackRetryEnvelope(
    string MessageType,
    string UniqueId,
    string? RequestId,
    string? IdempotencyKey,
    string Reason,
    DateTimeOffset QueuedAtUtc,
    DateTimeOffset RetryUntilUtc,
    int RetryAttempt,
    string CorrelationId,
    string WritebackPayload)
{
    public const string TypeName = "RtaWriteback";
}
