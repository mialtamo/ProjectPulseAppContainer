using ProjectPulse.Processor.Options;
using Microsoft.Extensions.Options;

namespace ProjectPulse.Processor.Services;

public sealed class OutboundCircuitBreaker(IOptions<ClaimProcessorOptions> options)
{
    private readonly ClaimProcessorOptions _options = options.Value;
    private readonly object _sync = new();
    private int _consecutiveFailures;
    private DateTimeOffset? _openUntil;

    public bool CanAttempt()
    {
        lock (_sync)
        {
            if (_openUntil is null)
            {
                return true;
            }

            if (DateTimeOffset.UtcNow >= _openUntil.Value)
            {
                _openUntil = null;
                _consecutiveFailures = Math.Max(0, _options.CircuitFailureThreshold - 1);
                return true;
            }

            return false;
        }
    }

    public void RecordSuccess()
    {
        lock (_sync)
        {
            _consecutiveFailures = 0;
            _openUntil = null;
        }
    }

    public void RecordFailure()
    {
        lock (_sync)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= Math.Max(1, _options.CircuitFailureThreshold))
            {
                _openUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, _options.CircuitOpenSeconds));
            }
        }
    }
}
