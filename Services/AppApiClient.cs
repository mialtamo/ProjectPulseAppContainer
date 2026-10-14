using System.Net;
using System.Text;
using System.Text.Json;
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

    public async Task<IReadOnlyList<JsonElement>> GetPendingClaimsAsync(CancellationToken cancellationToken)
    {
        return _options.IsMockApiMode()
            ? await GetMockPendingClaimsAsync(cancellationToken)
            : await GetRtaQueuedRequestsAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<JsonElement>> GetMockPendingClaimsAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("AppApi");
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

        if (_options.ShouldLogPollPayloads())
        {
            logger.LogInformation("App API mock payload: {Payload}", responseText);
        }

        if (string.IsNullOrWhiteSpace(responseText))
        {
            return [];
        }

        using var document = JsonDocument.Parse(responseText);
        var records = ParseRecords(document.RootElement);

        telemetry.TrackMetric("RtaQueuedRecordsReturned", records.Count);
        logger.LogInformation("App API mock polling returned {Count} record(s)", records.Count);
        return records;
    }

    private async Task<IReadOnlyList<JsonElement>> GetRtaQueuedRequestsAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("AppApi");
        var pageSize = _options.GetClampedPageSize();
        var pagesPerPoll = _options.GetClampedPagesPerPoll();
        var records = new List<JsonElement>(pageSize * pagesPerPoll);

        for (var page = 1; page <= pagesPerPoll; page++)
        {
            var separator = _options.RtaQueuedPath.Contains('?') ? '&' : '?';
            var path = $"{_options.RtaQueuedPath}{separator}page={page}&limit={pageSize}";

            logger.LogInformation("Polling RTA queued endpoint GET {Path}", path);

            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            await authHeaderProvider.ApplyBearerTokenAsync(request, _options.AppApiScope, cancellationToken);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

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

            if (_options.ShouldLogPollPayloads())
            {
                logger.LogInformation("RTA queued page {Page} payload: {Payload}", page, responseText);
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
        return records;
    }

    private static IReadOnlyList<JsonElement> ParseRecords(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray().Select(item => item.Clone()).ToArray();
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("Items", out var items) &&
            items.ValueKind == JsonValueKind.Array)
        {
            return items.EnumerateArray().Select(item => item.Clone()).ToArray();
        }

        throw new JsonException("App API response was not an array and did not contain an Items array.");
    }

    public async Task<bool> WriteStatusAsync(string correlationId, string responseJson, CancellationToken cancellationToken)
    {
        if (!_options.RtaStatusWritebackEnabled)
        {
            logger.LogInformation(
                "RTA status writeback is disabled. CorrelationId={CorrelationId}",
                correlationId);
            return true;
        }

        var client = httpClientFactory.CreateClient("AppApi");
        var path = _options.RtaStatusPathTemplate.Replace(
            "{correlationId}",
            Uri.EscapeDataString(correlationId),
            StringComparison.OrdinalIgnoreCase);

        for (var attempt = 1; attempt <= Math.Max(1, _options.ResultWriteMaxAttempts); attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Put, path)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                };

                request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
                await authHeaderProvider.ApplyBearerTokenAsync(request, _options.AppApiScope, cancellationToken);

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                logger.LogWarning(
                    "RTA status writeback failed for {CorrelationId}. Attempt {Attempt}/{MaxAttempts}, status {StatusCode}",
                    correlationId,
                    attempt,
                    _options.ResultWriteMaxAttempts,
                    (int)response.StatusCode);

                if (!IsTransient(response.StatusCode))
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(
                    ex,
                    "RTA status writeback exception for {CorrelationId}. Attempt {Attempt}/{MaxAttempts}",
                    correlationId,
                    attempt,
                    _options.ResultWriteMaxAttempts);
            }

            if (attempt < _options.ResultWriteMaxAttempts)
            {
                await Task.Delay(_options.ResultWriteRetryDelayMilliseconds * attempt, cancellationToken);
            }
        }

        telemetry.TrackEvent("RtaStatusWritebackFailed", new Dictionary<string, string>
        {
            ["correlationId"] = correlationId
        });

        return false;
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        (int)statusCode == 429 ||
        (int)statusCode >= 500;
}
