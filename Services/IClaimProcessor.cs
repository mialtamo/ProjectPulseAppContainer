using System.Text.Json;
using ProjectPulse.Processor.Models;

namespace ProjectPulse.Processor.Services;

public interface IClaimProcessor
{
    Task<IReadOnlyList<ClaimProcessingResult>> ProcessBatchAsync(
        IReadOnlyList<JsonElement> claims,
        IReadOnlyList<string> rtaResponsePayloads,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ClaimProcessingResult>> BufferBatchAsync(
        IReadOnlyList<JsonElement> claims,
        string reason,
        IReadOnlyList<string> rtaResponsePayloads,
        CancellationToken cancellationToken);
}

