using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectPulse.Processor.Options;
using Microsoft.Extensions.Options;

namespace ProjectPulse.Processor.Services;

public sealed class PayloadTransformer(IOptions<ClaimProcessorOptions> options)
{
    private readonly Dictionary<string, string> _mapping = options.Value.GetApimFieldMapping();

    public JsonObject Transform(JsonElement source)
    {
        var output = new JsonObject();

        foreach (var (targetField, sourcePath) in _mapping)
        {
            if (TryGetValue(source, sourcePath, out var value))
            {
                output[targetField] = JsonNode.Parse(value.GetRawText());
            }
        }

        return output;
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
