using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectPulse.Processor.Options;
using Microsoft.Extensions.Options;

namespace ProjectPulse.Processor.Services;

public sealed class PayloadTransformer(IOptions<ClaimProcessorOptions> options)
{
    private readonly ClaimProcessorOptions _options = options.Value;

    public JsonObject Transform(JsonElement source)
    {
        var requestType = TryGetValue(source, "REQUEST_TYPE", out var requestTypeValue)
            ? requestTypeValue.GetString()
            : null;
        var mapping = _options.GetApimFieldMapping(requestType);
        var output = new JsonObject();

        foreach (var (targetField, sourcePath) in mapping)
        {
            if (TryGetValue(source, sourcePath, out var value) && IsPresent(value))
            {
                SetTargetValue(output, targetField, JsonNode.Parse(value.GetRawText()));
            }
        }

        if (string.Equals(requestType, "INITIAL_REQUEST", StringComparison.OrdinalIgnoreCase) &&
            !output.ContainsKey("Program"))
        {
            output["Program"] = "CHAMPVA";
        }

        return output;
    }

    private static bool IsPresent(JsonElement value) =>
        value.ValueKind != JsonValueKind.Null &&
        (value.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(value.GetString()));

    private static void SetTargetValue(JsonObject output, string targetPath, JsonNode? value)
    {
        var segments = targetPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return;
        }

        var target = output;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (target[segments[i]] is not JsonObject child)
            {
                child = new JsonObject();
                target[segments[i]] = child;
            }

            target = child;
        }

        target[segments[^1]] = value;
    }

    private static bool TryGetValue(JsonElement root, string path, out JsonElement value)
    {
        value = root;

        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty(segment, out var next))
            {
                value = default;
                return false;
            }

            value = next;
        }

        return true;
    }
}
