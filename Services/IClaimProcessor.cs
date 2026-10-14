using System.Text.Json;
using ProjectPulse.Processor.Models;

namespace ProjectPulse.Processor.Services;

public interface IClaimProcessor
{
    Task<IReadOnlyList<ClaimProcessingResult>> ProcessBatchAsync(
        IReadOnlyList<JsonElement> claims,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ClaimProcessingResult>> BufferBatchAsync(
        IReadOnlyList<JsonElement> claims,
        string reason,
        CancellationToken cancellationToken);
}
