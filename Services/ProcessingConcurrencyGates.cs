using Microsoft.Extensions.Options;
using ProjectPulse.Processor.Options;

namespace ProjectPulse.Processor.Services;

public sealed class ProcessingConcurrencyGates(IOptions<ClaimProcessorOptions> options)
{
    private readonly ClaimProcessorOptions _options = options.Value;

    public ProcessingConcurrencyMode Mode => _options.GetProcessingConcurrencyMode();

    public int MaxConcurrentMessages => Mode == ProcessingConcurrencyMode.Sequential
        ? 1
        : _options.GetProcessingMaxConcurrency();

    public SemaphoreSlim ApimRequest { get; } = CreateApimRequestGate(options.Value);

    public SemaphoreSlim RtaWriteback { get; } = CreateRtaWritebackGate(options.Value);

    private static SemaphoreSlim CreateApimRequestGate(ClaimProcessorOptions options)
    {
        var concurrency = options.GetProcessingConcurrencyMode() is ProcessingConcurrencyMode.ApimOnly or ProcessingConcurrencyMode.Full
            ? options.GetProcessingMaxConcurrency()
            : 1;
        return new SemaphoreSlim(concurrency, concurrency);
    }

    private static SemaphoreSlim CreateRtaWritebackGate(ClaimProcessorOptions options)
    {
        var concurrency = options.GetProcessingConcurrencyMode() is ProcessingConcurrencyMode.RtaOnly or ProcessingConcurrencyMode.Full
            ? options.GetProcessingMaxConcurrency()
            : 1;
        return new SemaphoreSlim(concurrency, concurrency);
    }
}
