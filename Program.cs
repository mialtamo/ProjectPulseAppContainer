using System.Text.Json;
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
    context.Response.Headers.CacheControl = "no-store";
    var items = await store.ListAsync(1000, cancellationToken);
    return Results.Json(items, historyJsonOptions);
});

app.MapGet("/requests/{id}", async (HttpContext context, string id, IRequestHistoryStore store, CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var item = await store.GetAsync(id, cancellationToken);
    return item is null
        ? Results.NotFound()
        : Results.Json(item, historyJsonOptions);
});

app.MapGet("/requests.html", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'";

    return Results.Content(UiPages.RequestHistoryPage, "text/html; charset=utf-8");
});

app.MapGet("/live", () => Results.Ok(new { status = "ok", role = projectPulseRoleName }));

app.Run();
