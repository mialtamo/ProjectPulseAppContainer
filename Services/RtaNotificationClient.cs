using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProjectPulse.Processor.Options;

namespace ProjectPulse.Processor.Services;

public interface IRtaNotificationClient
{
    Task<bool> SendFailureAsync(
        JsonElement? rtaItem,
        string failureSource,
        string failureType,
        string emailBody,
        IReadOnlyDictionary<string, string> attributes,
        CancellationToken cancellationToken);
}

public sealed class RtaNotificationClient(
    IHttpClientFactory httpClientFactory,
    IAuthHeaderProvider authHeaderProvider,
    IOptions<ClaimProcessorOptions> options,
    ILogger<RtaNotificationClient> logger) : IRtaNotificationClient
{
    private const string Subject = "Error occurred within RTA DocMP Integration Application";
    private static readonly JsonSerializerOptions NotificationJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly ClaimProcessorOptions _options = options.Value;

    public async Task<bool> SendFailureAsync(
        JsonElement? rtaItem,
        string failureSource,
        string failureType,
        string emailBody,
        IReadOnlyDictionary<string, string> attributes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.RTANotificationsBaseURL))
        {
            logger.LogError(
                "RTA notification was not sent because RTANotificationsBaseURL is not configured. Source={Source} Type={Type}",
                failureSource,
                failureType);
            return false;
        }

        var path = string.IsNullOrWhiteSpace(_options.RTANotificationsPath)
            ? "/admin/notifications"
            : "/" + _options.RTANotificationsPath.Trim('/');
        Uri endpoint;
        try
        {
            endpoint = new Uri(_options.RTANotificationsBaseURL.TrimEnd('/') + path, UriKind.Absolute);
        }
        catch (UriFormatException ex)
        {
            logger.LogError(
                ex,
                "RTA notification was not sent because RTANotificationsBaseURL is invalid.");
            return false;
        }

        var notificationAttributes = new List<NotificationAttribute>
        {
            new("FAILURE_SOURCE", failureSource),
            new("FAILURE_TYPE", failureType)
        };

        AddItemAttribute(rtaItem, notificationAttributes, "REQUEST_TYPE");
        AddItemAttribute(rtaItem, notificationAttributes, "CORRELATION_ID");
        AddItemAttribute(rtaItem, notificationAttributes, "IDEMPOTENCY_KEY");
        foreach (var attribute in attributes)
        {
            notificationAttributes.Add(new NotificationAttribute(
                attribute.Key,
                Limit(attribute.Value, 1000)));
        }

        var payload = new NotificationPayload(
            ExternalCorrelationId: null,
            EmailSubject: Subject,
            EmailBody: Limit(emailBody, 4000),
            RequestId: GetLong(rtaItem, "REQUEST_ID"),
            RequestQueueId: GetLong(rtaItem, "REQUEST_QUEUE_ID"),
            NotificationAttrs: notificationAttributes);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, NotificationJsonOptions),
            Encoding.UTF8,
            "application/json");

        try
        {
            await authHeaderProvider.ApplyBearerTokenAsync(request, _options.AppApiScope, cancellationToken);
            var client = httpClientFactory.CreateClient("RtaNotifications");
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                logger.LogError(
                    "RTA admin notification failed. HTTP {StatusCode} from {Endpoint}. Source={Source} Type={Type}",
                    (int)response.StatusCode,
                    endpoint,
                    failureSource,
                    failureType);
                return false;
            }

            logger.LogWarning(
                "RTA admin notification sent. Source={Source} Type={Type} RequestId={RequestId} RequestQueueId={RequestQueueId}",
                failureSource,
                failureType,
                payload.RequestId,
                payload.RequestQueueId);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "RTA admin notification request failed. Endpoint={Endpoint} Source={Source} Type={Type}",
                endpoint,
                failureSource,
                failureType);
            return false;
        }
    }

    private static void AddItemAttribute(
        JsonElement? item,
        ICollection<NotificationAttribute> attributes,
        string propertyName)
    {
        var value = GetScalar(item, propertyName);
        if (!string.IsNullOrWhiteSpace(value))
        {
            attributes.Add(new NotificationAttribute(propertyName, Limit(value, 1000)));
        }
    }

    private static long? GetLong(JsonElement? item, string propertyName)
    {
        var value = GetScalar(item, propertyName);
        return long.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string? GetScalar(JsonElement? item, string propertyName)
    {
        if (!item.HasValue || item.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in item.Value.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) ||
                property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            return property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : property.Value.ToString();
        }

        return null;
    }

    private static string Limit(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private sealed record NotificationPayload(
        [property: JsonPropertyName("EXTERNAL_CORRELATION_ID")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        string? ExternalCorrelationId,
        [property: JsonPropertyName("EMAIL_SUBJECT")] string EmailSubject,
        [property: JsonPropertyName("EMAIL_BODY")] string EmailBody,
        [property: JsonPropertyName("REQUEST_ID")] long? RequestId,
        [property: JsonPropertyName("REQUEST_QUEUE_ID")] long? RequestQueueId,
        [property: JsonPropertyName("NOTIFICATION_ATTRS")] IReadOnlyList<NotificationAttribute> NotificationAttrs);

    private sealed record NotificationAttribute(
        [property: JsonPropertyName("NAME")] string Name,
        [property: JsonPropertyName("VALUE")] string Value);
}
