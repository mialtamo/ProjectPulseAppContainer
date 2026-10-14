namespace ProjectPulse.Processor.Models;

public sealed record RequestHistoryItem(
    string RecordId,
    string BatchId,
    string UniqueId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string Status,
    string Destination,
    int? HttpStatusCode,
    string? BusinessResponseCode,
    string? Reason,
    DateTimeOffset? SentToApimAtUtc,
    DateTimeOffset? ApimResponseAtUtc,
    DateTimeOffset? BufferedToServiceBusAtUtc,
    bool PayloadLoggingEnabled,
    string? ApimRequestPayload,
    string? ApimResponsePayload);
