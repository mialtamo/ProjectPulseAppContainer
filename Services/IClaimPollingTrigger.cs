namespace ProjectPulse.Processor.Services;

public interface IClaimPollingTrigger
{
    bool TryRequestPoll();

    bool ConsumePendingPoll();

    Task<bool> WaitForManualPollAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
