using System.Text.Json;
using ProjectPulse.Processor.Models;
using ProjectPulse.Processor.Options;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProjectPulse.Processor.Services;

public sealed class HealthGate(
    IHttpClientFactory httpClientFactory,
    IAuthHeaderProvider authHeaderProvider,
    IOptions<ClaimProcessorOptions> options,
    TelemetryClient telemetry,
    ILogger<HealthGate> logger) : IHealthGate
{
    private readonly ClaimProcessorOptions _options = options.Value;

    public async Task<(HealthResult AppApi, HealthResult Apim)> CheckAsync(CancellationToken cancellationToken)
    {
        var appHealthTask = ProbeRtaHealthAsync("RtaApi", _options.RtaHealthPath, cancellationToken);
        var dbHealthTask = ProbeRtaHealthAsync("RtaDbApp", _options.RtaDbHealthPath, cancellationToken);
        var apimTask = ProbeHttpAsync(
            "ApimAndBackend",
            httpClientFactory.CreateClient("Apim"),
            _options.ApimHealthPath,
            _options.ApimScope,
            cancellationToken);

        await Task.WhenAll(appHealthTask, dbHealthTask, apimTask);

        var appHealth = await appHealthTask;
        var dbHealth = await dbHealthTask;
        var apimHealth = await apimTask;

        var combinedRta = Report(new HealthResult(
            appHealth.IsHealthy && dbHealth.IsHealthy,
            "RtaApiAndDb",
            Detail: $"API={appHealth.IsHealthy}; DB/App={dbHealth.IsHealthy}"));

        return (combinedRta, apimHealth);
    }

    private async Task<HealthResult> ProbeRtaHealthAsync(
        string dependency,
        string path,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("AppApi");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            await authHeaderProvider.ApplyBearerTokenAsync(request, _options.AppApiScope, cancellationToken);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            var healthy = response.IsSuccessStatusCode && IsHealthyPayload(body);
            return Report(new HealthResult(
                healthy,
                dependency,
                (int)response.StatusCode,
                healthy ? "IS_HEALTHY=true" : body));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Health check failed for {Dependency}", dependency);
            return Report(new HealthResult(false, dependency, Detail: ex.Message));
        }
    }

    private async Task<HealthResult> ProbeHttpAsync(
        string dependency,
        HttpClient client,
        string path,
        string scope,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Report(new HealthResult(true, dependency, Detail: "Health probe disabled"));
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            await authHeaderProvider.ApplyBearerTokenAsync(request, scope, cancellationToken);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            return Report(new HealthResult(
                response.IsSuccessStatusCode,
                dependency,
                (int)response.StatusCode,
                response.ReasonPhrase));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Health check failed for {Dependency}", dependency);
            return Report(new HealthResult(false, dependency, Detail: ex.Message));
        }
    }

    private static bool IsHealthyPayload(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.ValueKind == JsonValueKind.Object &&
               document.RootElement.TryGetProperty("IS_HEALTHY", out var healthy) &&
               healthy.ValueKind == JsonValueKind.True;
    }

    private HealthResult Report(HealthResult result)
    {
        telemetry.TrackMetric($"DependencyHealth.{result.Dependency}", result.IsHealthy ? 1 : 0);
        telemetry.TrackEvent(
            "DependencyHealth",
            new Dictionary<string, string>
            {
                ["dependency"] = result.Dependency,
                ["healthy"] = result.IsHealthy.ToString(),
                ["statusCode"] = result.StatusCode?.ToString() ?? string.Empty,
                ["detail"] = result.Detail ?? string.Empty
            });

        if (!result.IsHealthy)
        {
            logger.LogWarning(
                "Dependency {Dependency} is unhealthy. Status={StatusCode} Detail={Detail}",
                result.Dependency,
                result.StatusCode,
                result.Detail);
        }

        return result;
    }
}
