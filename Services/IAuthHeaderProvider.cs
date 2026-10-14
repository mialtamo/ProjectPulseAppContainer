namespace ProjectPulse.Processor.Services;

public interface IAuthHeaderProvider
{
    Task ApplyBearerTokenAsync(HttpRequestMessage request, string scope, CancellationToken cancellationToken);
}
