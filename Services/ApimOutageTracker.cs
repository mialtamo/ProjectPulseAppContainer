using ProjectPulse.Processor.Options;
using Microsoft.Extensions.Options;

namespace ProjectPulse.Processor.Services;

public enum ApimOutageAction
{
    SendToApim,
    BufferToServiceBus,
    StopPolling
}

public sealed record ApimOutageDecision(
    ApimOutageAction Action,
    double OutageSeconds,
    string Reason,
    bool OutageStarted = false,
    bool Recovered = false);

public sealed class ApimOutageTracker(IOptions<ClaimProcessorOptions> options)
{
    private readonly ClaimProcessorOptions _options = options.Value;
    private readonly object _sync = new();
    private DateTimeOffset? _unhealthySince;
    private bool _bufferFailureDetected;

    public ApimOutageDecision Evaluate(bool apimHealthy)
    {
        lock (_sync)
        {
            if (apimHealthy)
            {
                var recovered = _unhealthySince is not null || _bufferFailureDetected;
                _unhealthySince = null;
                _bufferFailureDetected = false;

                return new ApimOutageDecision(
                    ApimOutageAction.SendToApim,
                    0,
                    recovered ? "ApimRecovered" : "ApimHealthy",
                    Recovered: recovered);
            }

            var started = false;
            if (_unhealthySince is null)
            {
                _unhealthySince = DateTimeOffset.UtcNow;
                started = true;
            }

            var outageSeconds = Math.Max(0, (DateTimeOffset.UtcNow - _unhealthySince.Value).TotalSeconds);

            if (_bufferFailureDetected)
            {
                return new ApimOutageDecision(
                    ApimOutageAction.StopPolling,
                    outageSeconds,
                    "ServiceBusBufferFailure",
                    OutageStarted: started);
            }

            return _options.GetApimOutageMode() switch
            {
                ApimOutageMode.StopImmediately => new ApimOutageDecision(
                    ApimOutageAction.StopPolling,
                    outageSeconds,
                    "ApimUnavailableStopImmediately",
                    OutageStarted: started),

                ApimOutageMode.BufferContinuously => new ApimOutageDecision(
                    ApimOutageAction.BufferToServiceBus,
                    outageSeconds,
                    "ApimUnavailableBufferContinuously",
                    OutageStarted: started),

                _ when outageSeconds >= _options.GetApimOutagePollStopSeconds() => new ApimOutageDecision(
                    ApimOutageAction.StopPolling,
                    outageSeconds,
                    "ApimOutageThresholdReached",
                    OutageStarted: started),

                _ => new ApimOutageDecision(
                    ApimOutageAction.BufferToServiceBus,
                    outageSeconds,
                    "ApimUnavailableBufferThenStop",
                    OutageStarted: started)
            };
        }
    }

    public void RecordApimFailure()
    {
        lock (_sync)
        {
            _unhealthySince ??= DateTimeOffset.UtcNow;
        }
    }

    public void RecordApimSuccess()
    {
        lock (_sync)
        {
            _unhealthySince = null;
            _bufferFailureDetected = false;
        }
    }

    public void RecordBufferFailure()
    {
        lock (_sync)
        {
            _bufferFailureDetected = true;
        }
    }
}
