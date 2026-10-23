using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectPulse.Processor.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProjectPulse.Processor.Services;

public sealed class PayloadTransformer(
    IOptions<ClaimProcessorOptions> options,
    ILogger<PayloadTransformer> logger)
{
    private readonly ClaimProcessorOptions _options = options.Value;

    public JsonObject Transform(JsonElement source)
    {
        var requestType = TryGetValue(source, "REQUEST_TYPE", out var requestTypeValue)
            ? requestTypeValue.GetString()
            : null;
        var mapping = _options.GetApimFieldMapping(requestType);
        var output = new JsonObject();
        var mappedTargets = new List<string>();
        var missingSources = new List<string>();

        var isInitialRequest = string.Equals(requestType, "INITIAL_REQUEST", StringComparison.OrdinalIgnoreCase);

        foreach (var (targetField, sourcePath) in mapping)
        {
            var wireTargetField = GetWireTargetField(targetField, isInitialRequest);
            if (wireTargetField is null)
            {
                continue;
            }

            var sourceFound = TryGetValue(source, sourcePath, out var value);

            if (sourceFound && IsPresent(value))
            {
                SetTargetValue(output, wireTargetField, JsonNode.Parse(value.GetRawText()));
                mappedTargets.Add(wireTargetField);
            }
            else
            {
                missingSources.Add(sourcePath);
            }
        }

        if (missingSources.Count > 0)
        {
            logger.LogWarning(
                "APIM mapping diagnostic: RequestType={RequestType}; MappingTargets={MappingTargets}; MappedTargets={MappedTargets}; MissingSourcePaths={MissingSourcePaths}",
                requestType ?? "(unknown)",
                string.Join(",", mapping.Keys),
                string.Join(",", mappedTargets),
                string.Join(",", missingSources));
        }
        else
        {
            logger.LogDebug(
                "APIM mapping diagnostic: RequestType={RequestType}; MappedTargets={MappedTargets}",
                requestType ?? "(unknown)",
                string.Join(",", mappedTargets));
        }

        if (isInitialRequest && !output.ContainsKey("program"))
        {
            output["program"] = "CHAMPVA";
        }

        return output;
    }

    private static string? GetWireTargetField(string targetField, bool isInitialRequest)
    {
        if (!isInitialRequest)
        {
            return targetField;
        }

        return targetField.ToLowerInvariant() switch
        {
            "patienticn" => "patientIcn",
            "sponsoricn" => "sponsorIcn",
            "startdateofservice" => "startDateOfService",
            "enddateofservice" => "endDateOfService",
            "program" => "program",
            "includedetails" => "includeDetails",
            "includeadjustmentdetails" => "includeDetails",
            _ => null
        };
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
            if (value.ValueKind != JsonValueKind.Object)
            {
                value = default;
                return false;
            }

            JsonElement next = default;
            var found = false;
            foreach (var property in value.EnumerateObject())
            {
                if (!string.Equals(property.Name, segment, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                next = property.Value;
                found = true;
                break;
            }

            if (!found)
            {
                value = default;
                return false;
            }

            value = next;
        }

        return true;
    }
}
