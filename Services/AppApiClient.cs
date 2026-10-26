using System.Net;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProjectPulse.Processor.Models;
using ProjectPulse.Processor.Options;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProjectPulse.Processor.Services;

public sealed class AppApiClient(
    IHttpClientFactory httpClientFactory,
    IAuthHeaderProvider authHeaderProvider,
    IOptions<ClaimProcessorOptions> options,
    TelemetryClient telemetry,
    ILogger<AppApiClient> logger) : IAppApiClient
{
    private readonly ClaimProcessorOptions _options = options.Value;

    public async Task<AppApiPollResult> GetPendingClaimsAsync(CancellationToken cancellationToken)
    {
        var pollStartedAtUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var result = _options.IsMockApiMode()
            ? await GetMockPendingClaimsAsync(cancellationToken)
            : await GetRtaQueuedRequestsAsync(cancellationToken);
        stopwatch.Stop();

        return result with
        {
            PollStartedAtUtc = pollStartedAtUtc,
            RtaPullDurationMs = stopwatch.ElapsedMilliseconds
        };
    }

    private async Task<AppApiPollResult> GetMockPendingClaimsAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("AppApi");
        // The mock endpoint uses maxRecords; the real RTA endpoint below uses page/limit.
        var maxRecords = _options.GetClampedPageSize();
        var separator = _options.RtaQueuedPath.Contains('?') ? '&' : '?';
        var path = $"{_options.RtaQueuedPath}{separator}maxRecords={maxRecords}";

        logger.LogInformation("Polling App API mock endpoint GET {Path}", path);

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        await authHeaderProvider.ApplyBearerTokenAsync(request, _options.AppApiScope, cancellationToken);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            telemetry.TrackEvent("AppApiMockPollFailed", new Dictionary<string, string>
            {
                ["statusCode"] = ((int)response.StatusCode).ToString()
            });

            throw new HttpRequestException(
                $"App API mock pending endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}",
                null,
                response.StatusCode);
        }

        if (_options.ShouldCaptureRequestHistoryPayloads())
        {
            logger.LogInformation("App API mock payload: {Payload}", _options.RedactRequestHistoryPayload(responseText));
        }

        if (string.IsNullOrWhiteSpace(responseText))
        {
            return new AppApiPollResult([], [responseText]);
        }

        using var document = JsonDocument.Parse(responseText);
        var records = ParseRecords(document.RootElement);

        telemetry.TrackMetric("RtaQueuedRecordsReturned", records.Count);
        logger.LogInformation("App API mock polling returned {Count} record(s)", records.Count);
        return new AppApiPollResult(records, [responseText]);
    }

    private async Task<AppApiPollResult> GetRtaQueuedRequestsAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("AppApi");
        var pageSize = _options.GetClampedPageSize();
        var pagesPerPoll = _options.GetClampedPagesPerPoll();
        var records = new List<JsonElement>(pageSize * pagesPerPoll);
        var responsePayloads = new List<string>(pagesPerPoll);

        for (var page = 1; page <= pagesPerPoll; page++)
        {
            var separator = _options.RtaQueuedPath.Contains('?') ? '&' : '?';
            var path = $"{_options.RtaQueuedPath}{separator}page={page}&limit={pageSize}";

            logger.LogInformation("Polling RTA queued endpoint GET {Path}", path);

            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            await authHeaderProvider.ApplyBearerTokenAsync(request, _options.AppApiScope, cancellationToken);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            responsePayloads.Add(responseText);

            if (!response.IsSuccessStatusCode)
            {
                telemetry.TrackEvent("RtaQueuedPollFailed", new Dictionary<string, string>
                {
                    ["statusCode"] = ((int)response.StatusCode).ToString(),
                    ["page"] = page.ToString()
                });

                throw new HttpRequestException(
                    $"RTA queued endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}",
                    null,
                    response.StatusCode);
            }

            if (_options.ShouldCaptureRequestHistoryPayloads())
            {
                logger.LogInformation("RTA queued page {Page} payload: {Payload}", page, _options.RedactRequestHistoryPayload(responseText));
            }

            if (string.IsNullOrWhiteSpace(responseText))
            {
                break;
            }

            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("Items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("RTA queued response did not contain an Items array.");
            }

            records.AddRange(items.EnumerateArray().Select(item => item.Clone()));

            var hasNextPage = root.TryGetProperty("HasNextPage", out var hasNext) &&
                              hasNext.ValueKind == JsonValueKind.True;

            logger.LogInformation(
                "RTA queued page {Page} returned {Count} record(s). HasNextPage={HasNextPage}",
                page,
                items.GetArrayLength(),
                hasNextPage);

            if (!hasNextPage)
            {
                break;
            }
        }

        telemetry.TrackMetric("RtaQueuedRecordsReturned", records.Count);
        logger.LogInformation("RTA queued polling returned {Count} record(s) total", records.Count);
        return new AppApiPollResult(records, responsePayloads);
    }

    private static IReadOnlyList<JsonElement> ParseRecords(JsonElement root)
    {
        var records = new List<JsonElement>();

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                AddRecords(item, records);
            }

            return records;
        }

        if (root.ValueKind == JsonValueKind.Object &&
            TryGetItemsArray(root, out var items))
        {
            return items.EnumerateArray().Select(item => item.Clone()).ToArray();
        }

        throw new JsonException("App API response was not an array and did not contain an Items array.");
    }

    private static void AddRecords(JsonElement item, ICollection<JsonElement> records)
    {
        if (item.ValueKind == JsonValueKind.Object &&
            TryGetItemsArray(item, out var items))
        {
            foreach (var record in items.EnumerateArray())
            {
                records.Add(record.Clone());
            }
            return;
        }

        records.Add(item.Clone());
    }

    private static bool TryGetItemsArray(JsonElement element, out JsonElement items)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, "Items", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Array)
                {
                    items = property.Value;
                    return true;
                }
            }
        }

        items = default;
        return false;
    }

    public async Task<RtaWritebackResult> WriteStatusAsync(string correlationId, string responseJson, CancellationToken cancellationToken)
    {
        if (!_options.RtaStatusWritebackEnabled)
        {
            logger.LogInformation(
                "RTA status writeback is disabled. CorrelationId={CorrelationId}",
                correlationId);
            return new RtaWritebackResult(false, null, 0, null, null, null);
        }

        var client = httpClientFactory.CreateClient("AppApi");
        var maxAttempts = Math.Max(1, _options.ResultWriteMaxAttempts);
        int? lastHttpStatusCode = null;
        int attemptsMade = 0;
        string? lastFailureKind = null;
        string? lastFailureMessage = null;
        var path = _options.RtaStatusPathTemplate.Replace(
            "{correlationId}",
            Uri.EscapeDataString(correlationId),
            StringComparison.OrdinalIgnoreCase);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            attemptsMade = attempt;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Put, path)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                };

                request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
                await authHeaderProvider.ApplyBearerTokenAsync(request, _options.AppApiScope, cancellationToken);

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                lastHttpStatusCode = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    return new RtaWritebackResult(true, true, attempt, lastHttpStatusCode, null, null);
                }

                lastFailureKind = "HttpStatusCode";
                var responseBody = _options.ShouldCaptureRequestHistoryPayloads()
                    ? await response.Content.ReadAsStringAsync(cancellationToken)
                    : null;
                if (responseBody is not null)
                {
                    responseBody = _options.RedactRequestHistoryPayload(responseBody);
                }

                lastFailureMessage = BuildHttpFailureMessage(response.StatusCode, response.ReasonPhrase, responseBody);

                logger.LogWarning(
                    "RTA status writeback failed for {CorrelationId}. Attempt {Attempt}/{MaxAttempts}, status {StatusCode}. {FailureMessage}",
                    correlationId,
                    attempt,
                    maxAttempts,
                    (int)response.StatusCode,
                    lastFailureMessage);

                if (!IsTransient(response.StatusCode))
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastFailureKind = ex is TaskCanceledException ? "Timeout" : "NetworkException";
                lastFailureMessage = $"{ex.GetType().Name}: {ex.Message}";
                logger.LogWarning(
                    ex,
                    "RTA status writeback exception for {CorrelationId}. Attempt {Attempt}/{MaxAttempts}",
                    correlationId,
                    attempt,
                    maxAttempts);
            }

            if (attempt < maxAttempts)
            {
                await Task.Delay(_options.ResultWriteRetryDelayMilliseconds * attempt, cancellationToken);
            }
        }

        logger.LogError(
            "RTA status writeback stopped after attempt {Attempts} of {MaxAttempts} for correlation ID {CorrelationId}. LastHttpStatusCode={LastHttpStatusCode}, LastFailureKind={LastFailureKind}",
            attemptsMade,
            maxAttempts,
            correlationId,
            lastHttpStatusCode,
            lastFailureKind ?? "Unknown");

        telemetry.TrackEvent("RtaStatusWritebackFailed", new Dictionary<string, string>
        {
            ["correlationId"] = correlationId
        });

        return new RtaWritebackResult(
            true,
            false,
            attemptsMade,
            lastHttpStatusCode,
            lastFailureKind ?? "Unknown",
            lastFailureMessage);
    }

    private static string BuildHttpFailureMessage(
        HttpStatusCode statusCode,
        string? reasonPhrase,
        string? responseBody)
    {
        var prefix = $"RTA returned HTTP {(int)statusCode} {reasonPhrase}".Trim();
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return prefix;
        }

        var details = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                AddProblemText(document.RootElement, "title", details);
                AddProblemText(document.RootElement, "detail", details);
                AddProblemText(document.RootElement, "message", details);

                if (TryGetPropertyIgnoreCase(document.RootElement, "errors", out var errors) &&
                    errors.ValueKind == JsonValueKind.Object)
                {
                    foreach (var field in errors.EnumerateObject().Take(8))
                    {
                        if (field.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var error in field.Value.EnumerateArray().Take(4))
                            {
                                if (error.ValueKind == JsonValueKind.String &&
                                    !string.IsNullOrWhiteSpace(error.GetString()))
                                {
                                    details.Add($"{field.Name}: {error.GetString()}");
                                }
                            }
                        }
                        else if (field.Value.ValueKind == JsonValueKind.String &&
                                 !string.IsNullOrWhiteSpace(field.Value.GetString()))
                        {
                            details.Add($"{field.Name}: {field.Value.GetString()}");
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Do not copy an unstructured error body into history or logs.
        }

        var summary = string.Join("; ", details.Distinct(StringComparer.Ordinal));
        if (string.IsNullOrWhiteSpace(summary))
        {
            return prefix;
        }

        summary = Regex.Replace(summary, @"\b\d{10}V\d{6}\b", "[REDACTED ICN]", RegexOptions.IgnoreCase);
        summary = Regex.Replace(summary, @"\b\d{3}-?\d{2}-?\d{4}\b", "[REDACTED]", RegexOptions.IgnoreCase);
        summary = Regex.Replace(summary, @"\bBearer\s+[^\s,;]+", "Bearer [REDACTED]", RegexOptions.IgnoreCase);
        summary = Regex.Replace(
            summary,
            @"(?i)(x-pulse-response-token\s*[:=]\s*)[^\s,;]+",
            "$1[REDACTED]");
        if (summary.Length > 800)
        {
            summary = summary[..800];
        }

        return $"{prefix}: {summary}";
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement root, string propertyName, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static void AddProblemText(JsonElement root, string propertyName, ICollection<string> details)
    {
        if (TryGetPropertyIgnoreCase(root, propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
        {
            details.Add(value.GetString()!);
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        (int)statusCode == 429 ||
        (int)statusCode >= 500;
}

