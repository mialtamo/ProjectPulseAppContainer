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
        var rtaTask = CheckRtaAsync(cancellationToken);
        var apimTask = CheckApimAsync(cancellationToken);
        await Task.WhenAll(rtaTask, apimTask);
        return (await rtaTask, await apimTask);
    }

    public async Task<(HealthResult RtaApi, HealthResult RtaDatabase, HealthResult Apim)> CheckDetailedAsync(
        CancellationToken cancellationToken)
    {
        var apiTask = ProbeRtaHealthAsync("RtaApi", _options.RtaHealthPath, cancellationToken);
        var databaseTask = ProbeRtaHealthAsync("RtaDbApp", _options.RtaDbHealthPath, cancellationToken);
        var apimTask = CheckApimAsync(cancellationToken);
        await Task.WhenAll(apiTask, databaseTask, apimTask);
        return (await apiTask, await databaseTask, await apimTask);
    }

    public async Task<HealthResult> CheckRtaAsync(CancellationToken cancellationToken)
    {
        var appHealthTask = ProbeRtaHealthAsync("RtaApi", _options.RtaHealthPath, cancellationToken);
        var dbHealthTask = ProbeRtaHealthAsync("RtaDbApp", _options.RtaDbHealthPath, cancellationToken);
        await Task.WhenAll(appHealthTask, dbHealthTask);

        var appHealth = await appHealthTask;
        var dbHealth = await dbHealthTask;
        return Report(new HealthResult(
            appHealth.IsHealthy && dbHealth.IsHealthy,
            "RtaApiAndDb",
            Detail: $"API={appHealth.IsHealthy}; DB/App={dbHealth.IsHealthy}"));
    }

    public Task<HealthResult> CheckApimAsync(CancellationToken cancellationToken) =>
        ProbeHttpAsync(
            "ApimAndBackend",
            httpClientFactory.CreateClient("Apim"),
            _options.ApimHealthPath,
            _options.ApimScope,
            cancellationToken);

    private async Task<HealthResult> ProbeRtaHealthAsync(
        string dependency,
        string path,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("AppApi");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, EndpointUri.Combine(client.BaseAddress!, path));
            RtaApiKeyHeader.Apply(request, _options);
            await authHeaderProvider.ApplyBearerTokenAsync(request, _options.AppApiScope, cancellationToken);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            var statusCode = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                return Report(new HealthResult(
                    false,
                    dependency,
                    statusCode,
                    $"HTTP {statusCode} {response.ReasonPhrase}".Trim()));
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                var healthy = IsHealthyPayload(body);
                return Report(new HealthResult(
                    healthy,
                    dependency,
                    statusCode,
                    healthy ? "Health payload reports IS_HEALTHY=true." : "HTTP response succeeded, but the health payload did not report IS_HEALTHY=true."));
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Health check returned invalid JSON for {Dependency}", dependency);
                return Report(new HealthResult(false, dependency, statusCode, "HTTP response succeeded, but the health payload was not valid JSON."));
            }
        }
        catch (TaskCanceledException ex)
        {
            logger.LogWarning(ex, "Health check timed out or was canceled for {Dependency}", dependency);
            var timeout = client.Timeout;
            var detail = timeout == Timeout.InfiniteTimeSpan
                ? "No response arrived before the probe deadline."
                : $"Request timed out after {timeout.TotalSeconds:0} seconds.";
            return Report(new HealthResult(false, dependency, Detail: detail));
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Health check failed before receiving an HTTP response for {Dependency}", dependency);
            return Report(new HealthResult(
                false,
                dependency,
                Detail: "No HTTP response. Check DNS, routing, firewall, TLS, and the replica log stream."));
        }
        catch (Azure.Identity.AuthenticationFailedException ex)
        {
            logger.LogWarning(ex, "Managed identity authentication failed for {Dependency}", dependency);
            return Report(new HealthResult(false, dependency, Detail: "Managed identity token acquisition failed."));
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
            return Report(new HealthResult(false, dependency, Detail: "Health probe path is not configured."));
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, EndpointUri.Combine(client.BaseAddress!, path));
            if (_options.IsApimAuthenticationEnabled())
            {
                await authHeaderProvider.ApplyBearerTokenAsync(request, scope, cancellationToken);
            }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            return Report(new HealthResult(
                response.IsSuccessStatusCode,
                dependency,
                (int)response.StatusCode,
                response.ReasonPhrase));
        }
        catch (TaskCanceledException ex)
        {
            logger.LogWarning(ex, "Health check timed out or was canceled for {Dependency}", dependency);
            return Report(new HealthResult(false, dependency, Detail: "No response arrived before the probe deadline."));
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Health check failed before receiving an HTTP response for {Dependency}", dependency);
            return Report(new HealthResult(
                false,
                dependency,
                Detail: "No HTTP response. Check DNS, routing, firewall, TLS, and the replica log stream."));
        }
        catch (Azure.Identity.AuthenticationFailedException ex)
        {
            logger.LogWarning(ex, "Managed identity authentication failed for {Dependency}", dependency);
            return Report(new HealthResult(false, dependency, Detail: "Managed identity token acquisition failed."));
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
