using ProjectPulse.Processor.Services;

namespace ProjectPulse.Processor.ContainerApp;

public sealed class RequestHistoryAutoCleanupWorker : BackgroundService
{
    private readonly IRequestHistoryStore _historyStore;
    private readonly ILogger<RequestHistoryAutoCleanupWorker> _logger;

    public RequestHistoryAutoCleanupWorker(
        IRequestHistoryStore historyStore,
        ILogger<RequestHistoryAutoCleanupWorker> logger)
    {
        _historyStore = historyStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var nowUtc = DateTimeOffset.UtcNow;
                var settings = await _historyStore.TryClaimAutoCleanupRunAsync(nowUtc, stoppingToken);
                if (settings is not null)
                {
                    var cutoffUtc = nowUtc.AddMinutes(-settings.RetentionMinutes);
                    var result = await _historyStore.DeleteOlderThanAsync(cutoffUtc, stoppingToken);
                    _logger.LogInformation(
                        "Automatic request-history cleanup completed. RetentionMinutes={RetentionMinutes}; CheckIntervalMinutes={CheckIntervalMinutes}; DeletedRecords={DeletedRecords}; DeletedPayloadBlobs={DeletedPayloadBlobs}; PayloadCleanupFailures={PayloadCleanupFailures}",
                        settings.RetentionMinutes,
                        settings.CheckIntervalMinutes,
                        result.DeletedRecords,
                        result.DeletedPayloadBlobs,
                        result.PayloadCleanupFailures);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Automatic request-history cleanup failed");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
