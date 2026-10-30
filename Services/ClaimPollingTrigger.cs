namespace ProjectPulse.Processor.Services;

public sealed class ClaimPollingTrigger : IClaimPollingTrigger
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public bool TryRequestPoll()
    {
        try
        {
            _signal.Release();
            return true;
        }
        catch (SemaphoreFullException)
        {
            return false;
        }
    }

    public bool ConsumePendingPoll() => _signal.Wait(0);

    public Task<bool> WaitForManualPollAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _signal.WaitAsync(timeout, cancellationToken);
}
