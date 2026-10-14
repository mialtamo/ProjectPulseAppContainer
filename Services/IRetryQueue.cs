using ProjectPulse.Processor.Models;

namespace ProjectPulse.Processor.Services;

public interface IRetryQueue
{
    Task EnqueueAsync(RetryEnvelope envelope, CancellationToken cancellationToken);
}
