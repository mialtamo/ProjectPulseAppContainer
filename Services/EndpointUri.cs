using System;

namespace ProjectPulse.Processor.Services;

public static class EndpointUri
{
    public static Uri NormalizeBaseAddress(string value, string settingName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new UriFormatException($"{settingName} must be an absolute HTTP or HTTPS base URL without a query or fragment.");
        }

        var pathSegments = baseUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var normalizedPath = pathSegments.Length == 0
            ? "/"
            : "/" + string.Join("/", pathSegments) + "/";
        var normalizedBase = baseUri.GetLeftPart(UriPartial.Authority) + normalizedPath;
        return new Uri(normalizedBase, UriKind.Absolute);
    }

    public static string NormalizePath(string? path)
    {
        var value = path?.Trim() ?? string.Empty;
        var queryIndex = value.IndexOf('?');
        var route = queryIndex < 0 ? value : value[..queryIndex];
        var query = queryIndex < 0 ? string.Empty : value[queryIndex..];

        // On Linux, Uri.TryCreate can classify a slash-prefixed route (for example,
        // "/health") as an absolute file URI. Keep root-relative endpoint paths valid;
        // reject actual absolute URI values such as "https://host/health".
        if (!value.StartsWith("/", StringComparison.Ordinal)
            && Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            throw new UriFormatException("Endpoint paths must be relative to their configured base URL.");
        }

        var segments = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join("/", segments) + query;
    }

    public static Uri Combine(Uri baseAddress, string? path)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        var normalizedBase = NormalizeBaseAddress(baseAddress.ToString(), nameof(baseAddress));
        return new Uri(normalizedBase, NormalizePath(path));
    }
}
