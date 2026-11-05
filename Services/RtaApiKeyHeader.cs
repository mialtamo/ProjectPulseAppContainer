using ProjectPulse.Processor.Options;

namespace ProjectPulse.Processor.Services;

internal static class RtaApiKeyHeader
{
    public static void Apply(HttpRequestMessage request, ClaimProcessorOptions options)
    {
        // Read the exact lowercase environment variable directly as well as through configuration binding.
        var apiKey = Environment.GetEnvironmentVariable("x-api-key");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = options.XApiKey;
        }

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        }
    }
}
