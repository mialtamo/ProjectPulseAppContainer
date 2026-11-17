using Microsoft.Extensions.Configuration;

namespace ProjectPulse.Processor.Services;

public sealed record ConfigurationCheck(
    string Name,
    string Setting,
    string Status,
    string Details);

public sealed record ConfigurationChecklistResult(
    string Role,
    bool CanStartWorker,
    IReadOnlyList<ConfigurationCheck> Checks)
{
    public int BlockedCount => Checks.Count(check => check.Status == "Blocked");
    public int WarningCount => Checks.Count(check => check.Status == "Warning");
}

public sealed class ConfigurationChecklist(
    IConfiguration configuration,
    string role,
    bool roleIsValid)
{
    public ConfigurationChecklistResult Evaluate()
    {
        var checks = new List<ConfigurationCheck>();

        Add(
            checks,
            "Runtime role",
            "PROJECT_PULSE_ROLE",
            roleIsValid ? "Ready" : "Blocked",
            roleIsValid
                ? $"The {role} worker role is selected."
                : "Set PROJECT_PULSE_ROLE to RTA or SERVICEBUS.");

        AddUrl(checks, "RTA / App API base URL", "AppApiBaseUrl", required: true);
        AddUrl(checks, "APIM base URL", "ApimBaseUrl", required: true);

        var namespaceHost = Get("ServiceBusFullyQualifiedNamespace");
        var validNamespace = !string.IsNullOrWhiteSpace(namespaceHost)
            && Uri.CheckHostName(namespaceHost) == UriHostNameType.Dns
            && !namespaceHost.Contains("://", StringComparison.Ordinal)
            && !namespaceHost.Contains('/', StringComparison.Ordinal)
            && !namespaceHost.Contains(':', StringComparison.Ordinal);
        Add(
            checks,
            "Service Bus namespace",
            "ServiceBusFullyQualifiedNamespace",
            validNamespace ? "Ready" : "Blocked",
            validNamespace
                ? "A namespace hostname is configured."
                : "Set the namespace hostname only, without a scheme, port, or queue path.");

        var queueName = Get("ServiceBusQueueName");
        Add(
            checks,
            "Service Bus queue",
            "ServiceBusQueueName",
            string.IsNullOrWhiteSpace(queueName) ? "Blocked" : "Ready",
            string.IsNullOrWhiteSpace(queueName)
                ? "Set the queue name."
                : "A queue name is configured.");

        var historyStorage = Get("RequestHistoryStorageConnectionString")
            ?? Get("AzureWebJobsStorage");
        Add(
            checks,
            "Request history storage",
            "RequestHistoryStorageConnectionString",
            string.IsNullOrWhiteSpace(historyStorage) ? "Blocked" : "Ready",
            string.IsNullOrWhiteSpace(historyStorage)
                ? "Set RequestHistoryStorageConnectionString or AzureWebJobsStorage."
                : "A storage connection is configured. Its value is hidden.");

        var authValue = Get("APIM_AUTHENTICATION_ENABLED");
        var apimAuthValid = string.IsNullOrWhiteSpace(authValue) || bool.TryParse(authValue, out _);
        var apimAuthEnabled = !bool.TryParse(authValue, out var parsedAuth) || parsedAuth;
        Add(
            checks,
            "APIM authentication setting",
            "APIM_AUTHENTICATION_ENABLED",
            apimAuthValid ? "Ready" : "Blocked",
            apimAuthValid
                ? $"Bearer-token sending is {(apimAuthEnabled ? "enabled" : "disabled")}."
                : "Set this to true or false.");

        var apimScope = Get("ApimScope");
        Add(
            checks,
            "APIM token scope",
            "ApimScope",
            !apimAuthEnabled || !apimAuthValid || !string.IsNullOrWhiteSpace(apimScope) ? "Ready" : "Blocked",
            !apimAuthEnabled
                ? "Not required while APIM bearer-token sending is disabled."
                : string.IsNullOrWhiteSpace(apimScope)
                    ? "Set ApimScope to the API application ID URI followed by /.default."
                    : "An APIM scope is configured.");

        Add(
            checks,
            "APIM response token",
            "ApimResponseToken",
            string.IsNullOrWhiteSpace(Get("ApimResponseToken")) ? "Blocked" : "Ready",
            string.IsNullOrWhiteSpace(Get("ApimResponseToken"))
                ? "Set the APIM response-token secret; its value is never displayed."
                : "The response-token secret is configured.");

        var writebackValue = Get("RtaStatusWritebackEnabled");
        var writebackEnabled = bool.TryParse(writebackValue, out var parsedWriteback)
            ? parsedWriteback
            : false;
        var writebackValid = string.IsNullOrWhiteSpace(writebackValue) || bool.TryParse(writebackValue, out _);
        Add(
            checks,
            "RTA status writeback",
            "RtaStatusWritebackEnabled",
            !writebackValid || !writebackEnabled ? "Blocked" : "Ready",
            !writebackValid
                ? "Set RtaStatusWritebackEnabled to true or false."
                : !writebackEnabled
                    ? "Enable status writeback so completed and failed records can be resolved at RTA."
                    : "Status writeback is enabled.");

        var ttlValue = Get("ServiceBusMessageTtlMinutes");
        var ttl = int.TryParse(ttlValue, out var parsedTtl) ? parsedTtl : 10;
        var ttlValid = string.IsNullOrWhiteSpace(ttlValue) || int.TryParse(ttlValue, out _);
        Add(
            checks,
            "Service Bus record lifetime",
            "ServiceBusMessageTtlMinutes",
            ttlValid && ttl == 10 ? "Ready" : "Blocked",
            ttlValid && ttl == 10
                ? "The retry lifetime is set to the required 10-minute cap."
                : "Set ServiceBusMessageTtlMinutes to 10 to preserve the hard record deadline.");

        var notificationUrl = Get("RTANotificationsBaseURL");
        AddUrl(
            checks,
            "Admin notification endpoint",
            "RTANotificationsBaseURL",
            required: false,
            configuredValue: notificationUrl);

        var apiKey = Get("x-api-key");
        Add(
            checks,
            "RTA API key",
            "x-api-key",
            string.IsNullOrWhiteSpace(apiKey) ? "Warning" : "Ready",
            string.IsNullOrWhiteSpace(apiKey)
                ? "No key is configured. Add it if the RTA API requires x-api-key."
                : "The key is configured. Its value is hidden.");

        var scenario = Get("APIM_MOCK_SCENARIO");
        var validScenario = string.IsNullOrWhiteSpace(scenario)
            || scenario.Trim().ToLowerInvariant() is
                "success" or "docmp-not-found" or "docmp-error"
                or "http-404" or "http-429" or "http-500" or "http-503" or "http-504";
        Add(
            checks,
            "APIM mock scenario",
            "APIM_MOCK_SCENARIO",
            validScenario ? "Ready" : "Blocked",
            validScenario
                ? "The mock scenario setting is valid."
                : "Use a supported scenario or leave the setting blank.");

        var result = checks.AsReadOnly();
        return new ConfigurationChecklistResult(role, result.All(check => check.Status != "Blocked"), result);
    }

    private void AddUrl(
        ICollection<ConfigurationCheck> checks,
        string name,
        string setting,
        bool required,
        string? configuredValue = null)
    {
        var value = configuredValue ?? Get(setting);
        var valid = Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri is not null
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrWhiteSpace(uri.Host)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment);
        var status = valid ? "Ready" : required ? "Blocked" : "Warning";
        var details = valid
            ? "An absolute HTTP or HTTPS URL is configured."
            : required
                ? "Set a valid absolute HTTP or HTTPS URL without a query or fragment."
                : "Not configured. Admin notifications will not be delivered.";
        Add(checks, name, setting, status, details);
    }

    private string? Get(string key) => configuration[key];

    private static void Add(
        ICollection<ConfigurationCheck> checks,
        string name,
        string setting,
        string status,
        string details) =>
        checks.Add(new ConfigurationCheck(name, setting, status, details));
}
