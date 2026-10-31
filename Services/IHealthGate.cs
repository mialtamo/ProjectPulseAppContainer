using ProjectPulse.Processor.Models;

namespace ProjectPulse.Processor.Services;

public interface IHealthGate
{
    Task<(HealthResult AppApi, HealthResult Apim)> CheckAsync(CancellationToken cancellationToken);
    Task<HealthResult> CheckRtaAsync(CancellationToken cancellationToken);
    Task<HealthResult> CheckApimAsync(CancellationToken cancellationToken);
}
