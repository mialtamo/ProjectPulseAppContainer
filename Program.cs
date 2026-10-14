using System.Net.Sockets;
using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using ProjectPulse.Processor.ContainerApp;
using ProjectPulse.Processor.Options;
using ProjectPulse.Processor.Services;

var builder = WebApplication.CreateBuilder(args);

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

builder.Services.AddHostedService<ClaimPollingWorker>();

var app = builder.Build();

app.MapGet("/", () => Results.Content("""
<!doctype html>
<html><head><meta charset="utf-8"><title>Project Pulse</title>
<style>body{font-family:system-ui;background:#05070b;color:#e5eefc;padding:40px}a{color:#19ddff}code{color:#54e39b}</style>
</head><body>
<h1>Project Pulse - Container App</h1>
<p>Continuous claims polling worker is running.</p>
<p><a href="/pulse-health">Health</a> | <a href="/requests.html">Request history</a></p>
<p>Polling interval: <code>POLL_INTERVAL_SECONDS</code></p>
</body></html>
""", "text/html"));

app.MapGet("/pulse", () => Results.Redirect("/"));

app.MapGet("/pulse-health", async (IConfiguration configuration) =>
{
    static async Task<bool> CheckHttpAsync(string? baseUrl, string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return false;
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
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
        configuration["AppApiBaseUrl"],
        configuration["RtaHealthPath"] ?? configuration["AppApiHealthPath"] ?? "/health");

    var apim = await CheckHttpAsync(
        configuration["ApimBaseUrl"],
        configuration["ApimHealthPath"] ?? "/health");

    var serviceBus = await CheckServiceBusAsync(configuration["ServiceBusFullyQualifiedNamespace"]);

    return Results.Ok(new
    {
        appApi,
        apim,
        serviceBus,
        checkedAt = DateTimeOffset.UtcNow
    });
});

app.MapGet("/requests", async (IRequestHistoryStore store, CancellationToken cancellationToken) =>
{
    var items = await store.ListAsync(1000, cancellationToken);
    return Results.Ok(items);
});

app.MapGet("/requests/{id}", async (string id, IRequestHistoryStore store, CancellationToken cancellationToken) =>
{
    var item = await store.GetAsync(id, cancellationToken);
    return item is null ? Results.NotFound() : Results.Ok(item);
});

app.MapGet("/requests.html", () => Results.Content("""
<!doctype html>
<html><head><meta charset="utf-8"><title>Project Pulse Requests</title>
<style>
body{font-family:system-ui;background:#05070b;color:#e5eefc;padding:24px}
a{color:#19ddff}table{width:100%;border-collapse:collapse}th,td{padding:8px;border-bottom:1px solid #1c2a3a;text-align:left;font-size:12px}
th{color:#9fb4cf}pre{white-space:pre-wrap}
</style></head><body>
<p><a href="/">Home</a></p>
<h1>Project Pulse Requests</h1>
<table><thead><tr><th>Updated</th><th>Unique ID</th><th>Status</th><th>Destination</th><th>HTTP</th><th>Reason</th></tr></thead><tbody id="rows"></tbody></table>
<script>
async function load(){
  const r=await fetch('/requests',{cache:'no-store'});
  const items=await r.json();
  document.getElementById('rows').innerHTML=items.map(x=>'<tr><td>'+new Date(x.UpdatedAtUtc).toLocaleString()+'</td><td>'+String(x.UniqueId??'')+'</td><td>'+String(x.Status??'')+'</td><td>'+String(x.Destination??'')+'</td><td>'+String(x.HttpStatusCode??'')+'</td><td>'+String(x.Reason??'')+'</td></tr>').join('');
}
load(); setInterval(load,30000);
</script></body></html>
""", "text/html"));

app.Run();
