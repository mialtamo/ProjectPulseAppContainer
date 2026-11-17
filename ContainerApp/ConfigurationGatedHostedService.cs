using Microsoft.Extensions.Hosting;
using ProjectPulse.Processor.Services;

namespace ProjectPulse.Processor.ContainerApp;

/// <summary>
/// Keeps the web host available for diagnostics when configuration is incomplete,
/// while preventing a worker from starting with settings that cannot support processing.
/// </summary>
public sealed class ConfigurationGatedHostedService(
    IServiceProvider serviceProvider,
    ConfigurationChecklist checklist,
    ProjectPulseRole role,
    bool roleIsValid,
    bool requestHistoryStorageConfigured,
    ILogger<ConfigurationGatedHostedService> logger) : IHostedService
{
    private readonly List<IHostedService> _startedWorkers = [];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var report = checklist.Evaluate();
        if (!roleIsValid || !report.CanStartWorker)
        {
            var blocked = string.Join(
                "; ",
                report.Checks
                    .Where(check => check.Status == "Blocked")
                    .Select(check => $"{check.Setting}: {check.Details}"));

            logger.LogCritical(
                "Project Pulse web host is available, but the {Role} worker was not started because configuration is incomplete. {BlockedChecks}",
                report.Role,
                string.IsNullOrWhiteSpace(blocked) ? "The runtime role is invalid." : blocked);
            return;
        }

        try
        {
            IHostedService worker = role switch
            {
                ProjectPulseRole.Rta => serviceProvider.GetRequiredService<ClaimPollingWorker>(),
                ProjectPulseRole.ServiceBus => serviceProvider.GetRequiredService<ServiceBusWorker>(),
                _ => throw new InvalidOperationException($"Unsupported Project Pulse role '{role}'.")
            };

            await worker.StartAsync(cancellationToken);
            _startedWorkers.Add(worker);

            if (role == ProjectPulseRole.Rta && requestHistoryStorageConfigured)
            {
                var cleanupWorker = serviceProvider.GetRequiredService<RequestHistoryAutoCleanupWorker>();
                await cleanupWorker.StartAsync(cancellationToken);
                _startedWorkers.Add(cleanupWorker);
            }

            logger.LogInformation("Project Pulse {Role} worker started after configuration checks passed.", report.Role);
        }
        catch (Exception ex)
        {
            logger.LogCritical(
                ex,
                "Project Pulse web host is available, but the {Role} worker could not start. Open /checklist.html for configuration details.",
                report.Role);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        for (var index = _startedWorkers.Count - 1; index >= 0; index--)
        {
            try
            {
                await _startedWorkers[index].StopAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A Project Pulse worker failed while stopping.");
            }
        }
    }
}
