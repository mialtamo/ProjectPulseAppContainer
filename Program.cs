using System.Text.Json;
using System.Globalization;
using System.Net.Sockets;
using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using ProjectPulse.Processor.ContainerApp;
using ProjectPulse.Processor.Options;
using ProjectPulse.Processor.Services;
using ProjectPulse.Processor.Web;

var builder = WebApplication.CreateBuilder(args);
var projectPulseRole = ProjectPulseRoleResolver.Resolve();
var projectPulseRoleName = projectPulseRole.ToConfigurationValue();

builder.Services.AddApplicationInsightsTelemetry();

builder.Services.AddOptions<ClaimProcessorOptions>()
    .BindConfiguration(string.Empty)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());

builder.Services.AddHttpClient("AppApi", (sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<ClaimProcessorOptions>>().Value;
    client.BaseAddress = new Uri(options.AppApiBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(options.AppApiTimeoutSeconds);
});

builder.Services.AddHttpClient("Apim", (sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<ClaimProcessorOptions>>().Value;
    client.BaseAddress = new Uri(options.ApimBaseUrl);
    client.Timeout = Timeout.InfiniteTimeSpan;
});

builder.Services.AddHttpClient("RtaNotifications", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddHttpClient("HealthCheck", client =>
{
    client.Timeout = TimeSpan.FromSeconds(3);
});

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<ClaimProcessorOptions>>().Value;
    var credential = sp.GetRequiredService<TokenCredential>();
    return new ServiceBusClient(options.ServiceBusFullyQualifiedNamespace, credential);
});

builder.Services.AddSingleton<IAuthHeaderProvider, ManagedIdentityAuthHeaderProvider>();
builder.Services.AddSingleton<IHealthGate, HealthGate>();
builder.Services.AddSingleton<IAppApiClient, AppApiClient>();
builder.Services.AddSingleton<IRtaNotificationClient, RtaNotificationClient>();
builder.Services.AddSingleton<IRetryQueue, ServiceBusRetryQueue>();
builder.Services.AddSingleton<IRequestHistoryStore, AzureStorageRequestHistoryStore>();
builder.Services.AddSingleton<PayloadTransformer>();
builder.Services.AddSingleton<IClaimProcessor, ClaimProcessor>();
builder.Services.AddSingleton<OutboundCircuitBreaker>();
builder.Services.AddSingleton<ApimOutageTracker>();

if (projectPulseRole == ProjectPulseRole.Rta)
{
    builder.Services.AddHostedService<ClaimPollingWorker>();
}
else
{
    builder.Services.AddHostedService<ServiceBusWorker>();
}

var app = builder.Build();

app.MapGet("/", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
    context.Response.Headers.Pragma = "no-cache";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'none'; img-src 'self' https://raw.githubusercontent.com; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";

    return Results.Content(
        UiPages.LandingPage(
            Environment.GetEnvironmentVariable("PROJECT_PULSE_VERSION"),
            Environment.GetEnvironmentVariable("PROJECT_PULSE_ENVIRONMENT"),
            projectPulseRoleName),
        "text/html; charset=utf-8");
});

app.MapGet("/pulse", () => Results.Redirect("/"));

app.MapGet("/pulse-health", async (IConfiguration configuration, IHttpClientFactory httpClientFactory) =>
{
    static async Task<bool> CheckHttpAsync(
        IHttpClientFactory clientFactory,
        string? baseUrl,
        string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return false;
            using var client = clientFactory.CreateClient("HealthCheck");
            using var response = await client.GetAsync($"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}");
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    static async Task<bool> CheckServiceBusAsync(string? host)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(host, 5671, cts.Token);
            return client.Connected;
        }
        catch { return false; }
    }

    var appApi = await CheckHttpAsync(
        httpClientFactory,
        configuration["AppApiBaseUrl"],
        configuration["RtaHealthPath"] ?? configuration["AppApiHealthPath"] ?? "/health");

    var apim = await CheckHttpAsync(
        httpClientFactory,
        configuration["ApimBaseUrl"],
        configuration["ApimHealthPath"] ?? "/health");

    var serviceBus = await CheckServiceBusAsync(configuration["ServiceBusFullyQualifiedNamespace"]);

    return Results.Ok(new
    {
        appApi,
        apim,
        serviceBus,
        role = projectPulseRoleName,
        checkedAt = DateTimeOffset.UtcNow
    });
});

var historyJsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = null
};

app.MapGet("/requests", async (HttpContext context, IRequestHistoryStore store, CancellationToken cancellationToken) =>
{
    var authorization = RequireProjectPulseAdmin(context, redirectToLogin: false);
    if (authorization is not null) return authorization;

    context.Response.Headers.CacheControl = "no-store";
    var limit = int.TryParse(context.Request.Query["limit"], out var requestedLimit)
        ? Math.Clamp(requestedLimit, 1, 1000)
        : 100;
    var page = await store.ListAsync(
        limit,
        context.Request.Query["pageToken"].FirstOrDefault(),
        cancellationToken);
    if (page.NextPageToken is not null)
    {
        context.Response.Headers["X-Next-Page-Token"] = page.NextPageToken;
    }

    return Results.Json(page.Items, historyJsonOptions);
});

app.MapGet("/requests/averages", async (HttpContext context, IRequestHistoryStore store, CancellationToken cancellationToken) =>
{
    var authorization = RequireProjectPulseAdmin(context, redirectToLogin: false);
    if (authorization is not null) return authorization;

    context.Response.Headers.CacheControl = "no-store";
    var range = context.Request.Query["range"].FirstOrDefault() ?? "all";
    var nowUtc = DateTimeOffset.UtcNow;
    DateTimeOffset? fromUtc = null;
    DateTimeOffset? toUtc = null;
    var cacheKey = range;

    switch (range)
    {
        case "all":
            break;
        case "7d":
            fromUtc = nowUtc.AddDays(-7);
            toUtc = nowUtc;
            break;
        case "30d":
            fromUtc = nowUtc.AddDays(-30);
            toUtc = nowUtc;
            break;
        case "365d":
            fromUtc = nowUtc.AddDays(-365);
            toUtc = nowUtc;
            break;
        case "custom":
            if (!DateTimeOffset.TryParse(
                    context.Request.Query["fromUtc"].FirstOrDefault(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var customFromUtc) ||
                !DateTimeOffset.TryParse(
                    context.Request.Query["toUtc"].FirstOrDefault(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var customToUtc) ||
                customFromUtc > customToUtc)
            {
                return Results.BadRequest(new { error = "A valid custom date/time range is required." });
            }

            fromUtc = customFromUtc;
            toUtc = customToUtc;
            cacheKey = $"custom:{customFromUtc.UtcTicks}:{customToUtc.UtcTicks}";
            break;
        default:
            return Results.BadRequest(new { error = "Unsupported timing range." });
    }

    var averages = await store.GetTimingAveragesAsync(fromUtc, toUtc, cacheKey, cancellationToken);
    return Results.Json(averages, historyJsonOptions);
});

app.MapGet("/requests/{id}", async (HttpContext context, string id, IRequestHistoryStore store, CancellationToken cancellationToken) =>
{
    var authorization = RequireProjectPulseAdmin(context, redirectToLogin: false);
    if (authorization is not null) return authorization;

    context.Response.Headers.CacheControl = "no-store";
    var item = await store.GetAsync(id, cancellationToken);
    return item is null
        ? Results.NotFound()
        : Results.Json(item, historyJsonOptions);
});

app.MapGet("/requests.html", (HttpContext context) =>
{
    var authorization = RequireProjectPulseAdmin(context, redirectToLogin: true);
    if (authorization is not null) return authorization;

    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'";

    var signedInUser = context.Request.Headers["X-MS-CLIENT-PRINCIPAL-NAME"].ToString();
    return Results.Content(UiPages.RequestHistoryPageFor(signedInUser), "text/html; charset=utf-8");
});

app.MapGet("/live", () => Results.Ok(new { status = "ok", role = projectPulseRoleName }));

app.Run();

static IResult? RequireProjectPulseAdmin(HttpContext context, bool redirectToLogin)
{
    const string requiredRole = "ProjectPulse.Admin";
    var principalHeader = context.Request.Headers["X-MS-CLIENT-PRINCIPAL"].ToString();

    if (string.IsNullOrWhiteSpace(principalHeader))
    {
        if (!redirectToLogin)
        {
            return Results.Unauthorized();
        }

        var returnPath = $"{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
        var loginUrl = "/.auth/login/aad?post_login_redirect_uri=" + Uri.EscapeDataString(returnPath);
        return Results.Redirect(loginUrl);
    }

    try
    {
        var principalJson = Convert.FromBase64String(principalHeader);
        using var principal = JsonDocument.Parse(principalJson);

        if (!principal.RootElement.TryGetProperty("claims", out var claims) ||
            claims.ValueKind != JsonValueKind.Array)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        foreach (var claim in claims.EnumerateArray())
        {
            if (!claim.TryGetProperty("typ", out var type) ||
                !claim.TryGetProperty("val", out var value) ||
                !string.Equals(value.GetString(), requiredRole, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var claimType = type.GetString();
            if (string.Equals(claimType, "roles", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(claimType, "role", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    claimType,
                    "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
    catch (FormatException)
    {
        return Results.Unauthorized();
    }
    catch (JsonException)
    {
        return Results.Unauthorized();
    }
}
