using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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
    public string RTANotificationsBaseURL { get; init; } = string.Empty;
    public string RTANotificationsPath { get; init; } = "/admin/notifications";
    public int RtaPageSize { get; init; } = 100;
    public int RtaPagesPerPoll { get; init; } = 1;
    public bool RtaStatusWritebackEnabled { get; init; } = false;
    public string AppApiMode { get; init; } = "RTA";
    public bool LogPollPayloads { get; init; } = false;
    public string RequestHistoryPayloadMode { get; init; } = string.Empty;
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
    [ConfigurationKeyName("APIM_MOCK_SCENARIO")]
    public string ApimMockScenario { get; init; } = string.Empty;
    public string ApimOutageMode { get; init; } = "BUFFER_THEN_STOP";
    public int ApimOutagePollStopSeconds { get; init; } = 300;

    public bool IsApimMockScenarioValid() =>
        string.IsNullOrWhiteSpace(ApimMockScenario)
        || ApimMockScenario.Trim().ToLowerInvariant() is
            "success" or "docmp-not-found" or "docmp-error"
            or "http-404" or "http-429" or "http-500" or "http-503" or "http-504";

    public string CorrelationIdField { get; init; } = "CORRELATION_ID";

    public string ApimFieldMapping { get; init; } = """
    {
      "correlationId": "CORRELATION_ID",
      "requestId": "REQUEST_ID",
      "requestType": "REQUEST_TYPE",
      "patientIcn": "METADATA.PATIENT_ICN",
      "sponsorIcn": "METADATA.SPONSOR_ICN",
      "startDateOfService": "METADATA.START_DATE_OF_SERVICE",
      "endDateOfService": "METADATA.END_DATE_OF_SERVICE",
      "program": "METADATA.PROGRAM",
      "includeAdjustmentDetails": "METADATA.INCLUDE_ADJUSTMENT_DETAILS"
    }
    """;

    public string ApimInitialRequestFieldMapping { get; init; } = """
    {
      "patientIcn": "INITIAL_REQUEST_METADATA.PATIENT_ICN",
      "sponsorIcn": "INITIAL_REQUEST_METADATA.SPONSOR_ICN",
      "startDateOfService": "INITIAL_REQUEST_METADATA.START_DATE_OF_SERVICE",
      "endDateOfService": "INITIAL_REQUEST_METADATA.END_DATE_OF_SERVICE",
      "program": "INITIAL_REQUEST_METADATA.PROGRAM",
      "includeDetails": "INITIAL_REQUEST_METADATA.INCLUDE_ADJUSTMENT_DETAILS"
    }
    """;

    public string ApimUpdateRequestFieldMapping { get; init; } = """
    {
      "correlationId": "CORRELATION_ID",
      "requestId": "REQUEST_ID",
      "requestType": "REQUEST_TYPE",
      "patientIcn": "METADATA.PATIENT_ICN",
      "sponsorIcn": "METADATA.SPONSOR_ICN",
      "startDateOfService": "METADATA.START_DATE_OF_SERVICE",
      "endDateOfService": "METADATA.END_DATE_OF_SERVICE",
      "program": "METADATA.PROGRAM",
      "claimNumber": "METADATA.CLAIM_NUMBER",
      "RTA": "METADATA.RTA",
      "notes": "METADATA.NOTES",
      "adjustments.individual.delta": "METADATA.ADJ_IND_DEDUCTIBLE",
      "adjustments.family.delta": "METADATA.ADJ_FAM_DEDUCTIBLE",
      "adjustments.outOfPocket.delta": "METADATA.ADJ_OUT_OF_POCKET"
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
    public int ServiceBusReceiveBatchSize { get; init; } = 10;
    public int ServiceBusReceiveWaitSeconds { get; init; } = 5;
    public int ServiceBusDependencyRetrySeconds { get; init; } = 5;

    public int CircuitFailureThreshold { get; init; } = 5;
    public int CircuitOpenSeconds { get; init; } = 30;

    public int ResultWriteMaxAttempts { get; init; } = 3;
    public int ResultWriteRetryDelayMilliseconds { get; init; } = 500;

    public int GetClampedPageSize() => Math.Clamp(RtaPageSize, 1, 100);
    public int GetClampedPagesPerPoll() => Math.Clamp(RtaPagesPerPoll, 1, 3);
    public int GetServiceBusReceiveBatchSize() => Math.Clamp(ServiceBusReceiveBatchSize, 1, 100);
    public int GetServiceBusReceiveWaitSeconds() => Math.Clamp(ServiceBusReceiveWaitSeconds, 1, 60);
    public int GetServiceBusDependencyRetrySeconds() => Math.Clamp(ServiceBusDependencyRetrySeconds, 1, 300);

    public bool IsMockApiMode() =>
        string.Equals(
            Environment.GetEnvironmentVariable("APP_API_MODE") ?? AppApiMode,
            "MOCK",
            StringComparison.OrdinalIgnoreCase);

    public string GetRequestHistoryPayloadMode()
    {
        // The explicit mode takes precedence; malformed values fail closed to OFF.
        // Use the legacy boolean only when neither explicit setting is configured.
        var configuredMode =
            Environment.GetEnvironmentVariable("REQUEST_HISTORY_PAYLOAD_MODE")
            ?? RequestHistoryPayloadMode;

        if (!string.IsNullOrWhiteSpace(configuredMode))
        {
            var normalizedMode = configuredMode.Trim().ToUpperInvariant();
            return normalizedMode is "OFF" or "PARTIAL" or "FULL" ? normalizedMode : "OFF";
        }

        var legacyEnabled = bool.TryParse(
            Environment.GetEnvironmentVariable("LOG_POLL_PAYLOADS"),
            out var envValue)
            ? envValue
            : LogPollPayloads;

        return legacyEnabled ? "FULL" : "OFF";
    }

    public bool ShouldCaptureRequestHistoryPayloads() =>
        GetRequestHistoryPayloadMode() != "OFF";

    public string RedactRequestHistoryPayload(string payload)
    {
        if (GetRequestHistoryPayloadMode() != "PARTIAL")
        {
            return payload;
        }

        try
        {
            var parsed = JsonNode.Parse(payload);
            return parsed is null
                ? "\"********\""
                : RedactNode(parsed).ToJsonString();
        }
        catch (JsonException)
        {
            // A malformed payload cannot be safely inspected, so do not expose it in PARTIAL mode.
            return "\"********\"";
        }
    }

    private static JsonNode RedactNode(JsonNode node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject.ToList())
            {
                if (IsSensitivePayloadField(property.Key))
                {
                    jsonObject[property.Key] = "********";
                }
                else if (property.Value is JsonValue valueNode
                    && valueNode.TryGetValue<string>(out var stringValue))
                {
                    if (TryRedactNestedJson(stringValue, out var redactedNestedJson))
                    {
                        jsonObject[property.Key] = JsonValue.Create(redactedNestedJson);
                    }
                    else if (RedactText(stringValue) is { } redactedText && redactedText != stringValue)
                    {
                        jsonObject[property.Key] = JsonValue.Create(redactedText);
                    }
                }
                else if (property.Value is not null)
                {
                    RedactNode(property.Value);
                }
            }

            return jsonObject;
        }

        if (node is JsonArray jsonArray)
        {
            for (var index = 0; index < jsonArray.Count; index++)
            {
                if (jsonArray[index] is JsonValue valueNode
                    && valueNode.TryGetValue<string>(out var stringValue))
                {
                    if (TryRedactNestedJson(stringValue, out var redactedNestedJson))
                    {
                        jsonArray[index] = JsonValue.Create(redactedNestedJson);
                    }
                    else if (RedactText(stringValue) is { } redactedText && redactedText != stringValue)
                    {
                        jsonArray[index] = JsonValue.Create(redactedText);
                    }
                }
                else if (jsonArray[index] is not null)
                {
                    RedactNode(jsonArray[index]!);
                }
            }
        }

        return node;
    }

    private static string RedactText(string value)
    {
        var redacted = Regex.Replace(value, @"\b\d{10}V\d{6}\b", "********", RegexOptions.IgnoreCase);
        return Regex.Replace(redacted, @"\b\d{3}-?\d{2}-?\d{4}\b", "********");
    }

    // API_CALL_HISTORY stores request/response bodies as JSON strings, so redact
    // identifiers inside those embedded documents as well as ordinary JSON properties.
    private static bool TryRedactNestedJson(string value, out string redacted)
    {
        redacted = value;
        var trimmed = value.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return false;
        }

        try
        {
            var parsed = JsonNode.Parse(value);
            if (parsed is null)
            {
                return false;
            }

            redacted = RedactNode(parsed).ToJsonString();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsSensitivePayloadField(string fieldName)
    {
        var normalized = fieldName
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return normalized.Contains("icn", StringComparison.Ordinal)
            || normalized.Contains("ssn", StringComparison.Ordinal)
            || normalized is "dob" or "birthdate" or "dateofbirth"
            || normalized.Contains("memberid", StringComparison.Ordinal)
            || normalized.Contains("claimantid", StringComparison.Ordinal)
            || normalized.Contains("patientname", StringComparison.Ordinal)
            || normalized.Contains("sponsorname", StringComparison.Ordinal)
            || normalized is "firstname" or "middlename" or "lastname" or "membername";
    }

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

    public Dictionary<string, string> GetApimFieldMapping() => ParseFieldMapping(ApimFieldMapping);

    public Dictionary<string, string> GetApimFieldMapping(string? requestType) => requestType?.Trim().ToUpperInvariant() switch
    {
        "INITIAL_REQUEST" => ParseFieldMapping(ApimInitialRequestFieldMapping),
        "UPDATE_REQUEST" => ParseFieldMapping(ApimUpdateRequestFieldMapping),
        _ => GetApimFieldMapping()
    };

    private static Dictionary<string, string> ParseFieldMapping(string mapping) =>
        string.IsNullOrWhiteSpace(mapping)
            ? []
            : JsonSerializer.Deserialize<Dictionary<string, string>>(mapping) ?? [];

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
