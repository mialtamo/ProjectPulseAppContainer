namespace ProjectPulse.Processor.Models;

public sealed record RequestHistoryItem(
    string RecordId,
    string BatchId,
    string UniqueId,
    string? RtaRequestId,
    string? InputSource,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string Status,
    string Destination,
    string? ProcessingRole,
    int? HttpStatusCode,
    string? BusinessResponseCode,
    string? Reason,
    DateTimeOffset? SentToApimAtUtc,
    DateTimeOffset? ApimResponseAtUtc,
    DateTimeOffset? BufferedToServiceBusAtUtc,
    bool PayloadLoggingEnabled,
    string PayloadMode,
    string? ApimRequestPayload,
    string? ApimResponsePayload,
    IReadOnlyList<string>? RtaResponsePayloads,
    string? RtaStatusPayload,
    long? RtaPullDurationMs = null,
    long? TranslationDurationMs = null,
    long? ApimRoundTripDurationMs = null,
    long? RtaWritebackDurationMs = null,
    long? EndToEndDurationMs = null,
    bool? RtaWritebackEnabled = null,
    bool? RtaWritebackAttempted = null,
    bool? RtaWritebackSucceeded = null,
    int? RtaWritebackHttpStatusCode = null,
    int? RtaWritebackAttempts = null,
    string? RtaWritebackFailureKind = null,
    string? RtaWritebackFailureMessage = null);

public sealed record RequestHistoryPage(
    IReadOnlyList<RequestHistoryItem> Items,
    string? NextPageToken);

