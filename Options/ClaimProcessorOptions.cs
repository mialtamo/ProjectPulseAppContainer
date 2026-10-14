using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace ProjectPulse.Processor.Options;

public enum ApimOutageMode
{
    BufferThenStop,
    StopImmediately,
    BufferContinuously
}

public sealed class ClaimProcessorOptions
{
    [Required]
    public string AppApiBaseUrl { get; init; } = string.Empty;

    public string AppApiScope { get; init; } = string.Empty;
    public int AppApiTimeoutSeconds { get; init; } = 10;

    public string RtaQueuedPath { get; init; } = "/requests/queued";
    public string RtaHealthPath { get; init; } = "/healthchecks";
    public string RtaDbHealthPath { get; init; } = "/healthchecks/db/app";
    public string RtaStatusPathTemplate { get; init; } = "/requests/{correlationId}/status";
    public int RtaPageSize { get; init; } = 100;
    public int RtaPagesPerPoll { get; init; } = 1;
    public bool RtaStatusWritebackEnabled { get; init; } = false;
    public string AppApiMode { get; init; } = "RTA";
    public bool LogPollPayloads { get; init; } = false;
    public string RequestHistoryTableName { get; init; } = "ProjectPulseRequestHistory";
    public string RequestHistoryBlobContainerName { get; init; } = "projectpulse-history-payloads";

    [Required]
    public string ApimBaseUrl { get; init; } = string.Empty;

    public string ApimHealthPath { get; init; } = "/health";
    public string ApimAdjudicationPath { get; init; } = "/claims/adjudicate";
    public int ApimRequestTimeoutSeconds { get; init; } = 15;
    public string ApimScope { get; init; } = string.Empty;
    public string ApimResponseHeaderName { get; init; } = "X-Pulse-Response-Token";
    public string ApimResponseToken { get; init; } = string.Empty;
    public string ApimOutageMode { get; init; } = "BUFFER_THEN_STOP";
    public int ApimOutagePollStopSeconds { get; init; } = 300;

    public string CorrelationIdField { get; init; } = "CORRELATION_ID";
    public string ApimBatchRootProperty { get; init; } = "claims";

    public string ApimFieldMapping { get; init; } = """
    {
      "correlationId": "CORRELATION_ID",
      "requestId": "REQUEST_ID",
      "idempotencyKey": "IDEMPOTENCY_KEY",
      "requestType": "REQUEST_TYPE",
      "patientIcn": "METADATA.PATIENT_ICN",
      "sponsorIcn": "METADATA.SPONSOR_ICN",
      "startDateOfService": "METADATA.START_DATE_OF_SERVICE",
      "endDateOfService": "METADATA.END_DATE_OF_SERVICE",
      "program": "METADATA.PROGRAM",
      "includeAdjustmentDetails": "METADATA.INCLUDE_ADJUSTMENT_DETAILS"
    }
    """;

    public string RetryHttpStatusCodes { get; init; } = "404,408,429,500,502,503,504";
    public string JsonResponseCodeField { get; init; } = "RESPONSE_CODE";
    public string JsonRetryCodes { get; init; } = string.Empty;
    public string JsonAlertCodes { get; init; } = "419";

    [Required]
    public string ServiceBusFullyQualifiedNamespace { get; init; } = string.Empty;

    public string ServiceBusQueueName { get; init; } = "claim-retry";
    public int ServiceBusMessageTtlMinutes { get; init; } = 10;

    public int CircuitFailureThreshold { get; init; } = 5;
    public int CircuitOpenSeconds { get; init; } = 30;

    public int ResultWriteMaxAttempts { get; init; } = 3;
    public int ResultWriteRetryDelayMilliseconds { get; init; } = 500;

    public int GetClampedPageSize() => Math.Clamp(RtaPageSize, 1, 100);
    public int GetClampedPagesPerPoll() => Math.Clamp(RtaPagesPerPoll, 1, 3);

    public bool IsMockApiMode() =>
        string.Equals(
            Environment.GetEnvironmentVariable("APP_API_MODE") ?? AppApiMode,
            "MOCK",
            StringComparison.OrdinalIgnoreCase);

    public bool ShouldLogPollPayloads() =>
        bool.TryParse(
            Environment.GetEnvironmentVariable("LOG_POLL_PAYLOADS"),
            out var envValue)
            ? envValue
            : LogPollPayloads;

    public int GetApimOutagePollStopSeconds() =>
        int.TryParse(
            Environment.GetEnvironmentVariable("APIM_OUTAGE_POLL_STOP_SECONDS"),
            out var envValue)
            ? Math.Max(1, envValue)
            : Math.Max(1, ApimOutagePollStopSeconds);

    public global::ProjectPulse.Processor.Options.ApimOutageMode GetApimOutageMode()
    {
        var value = (Environment.GetEnvironmentVariable("APIM_OUTAGE_MODE") ?? ApimOutageMode)
            .Trim()
            .Replace("-", "_", StringComparison.Ordinal)
            .ToUpperInvariant();

        return value switch
        {
            "STOP_IMMEDIATELY" => global::ProjectPulse.Processor.Options.ApimOutageMode.StopImmediately,
            "BUFFER_CONTINUOUSLY" => global::ProjectPulse.Processor.Options.ApimOutageMode.BufferContinuously,
            _ => global::ProjectPulse.Processor.Options.ApimOutageMode.BufferThenStop
        };
    }

    public Dictionary<string, string> GetApimFieldMapping()
    {
        if (string.IsNullOrWhiteSpace(ApimFieldMapping))
        {
            return [];
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(ApimFieldMapping)
            ?? [];
    }

    public HashSet<int> GetRetryHttpStatusCodeSet() => ParseIntSet(RetryHttpStatusCodes);
    public HashSet<string> GetJsonRetryCodeSet() => ParseStringSet(JsonRetryCodes);
    public HashSet<string> GetJsonAlertCodeSet() => ParseStringSet(JsonAlertCodes);

    private static HashSet<int> ParseIntSet(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var parsed) ? parsed : -1)
            .Where(x => x >= 0)
            .ToHashSet();

    private static HashSet<string> ParseStringSet(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
