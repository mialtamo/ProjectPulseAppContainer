using System.Text.Json;
using ProjectPulse.Processor.Models;

namespace ProjectPulse.Processor.Services;

public interface IAppApiClient
{
    Task<AppApiPollResult> GetPendingClaimsAsync(CancellationToken cancellationToken);
    Task<RtaWritebackResult> WriteStatusAsync(string correlationId, string responseJson, CancellationToken cancellationToken);
}

