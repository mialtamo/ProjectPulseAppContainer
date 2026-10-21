using System.Text.Json;

namespace ProjectPulse.Processor.Models;

public sealed record AppApiPollResult(
    IReadOnlyList<JsonElement> Claims,
    IReadOnlyList<string> RtaResponsePayloads);

