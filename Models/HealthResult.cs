namespace ProjectPulse.Processor.Models;

public sealed record HealthResult(bool IsHealthy, string Dependency, int? StatusCode = null, string? Detail = null);
