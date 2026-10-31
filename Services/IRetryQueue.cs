using ProjectPulse.Processor.Models;

namespace ProjectPulse.Processor.Services;

public interface IRetryQueue
{
    Task EnqueueAsync(RetryEnvelope envelope, CancellationToken cancellationToken);
    Task EnqueueRtaWritebackAsync(RtaWritebackRetryEnvelope envelope, CancellationToken cancellationToken);
    Task ScheduleRtaWritebackRetryAsync(RtaWritebackRetryEnvelope envelope, TimeSpan delay, CancellationToken cancellationToken);
}
