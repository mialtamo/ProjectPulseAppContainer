using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProjectPulse.Processor.Models;
using ProjectPulse.Processor.Options;
using ProjectPulse.Processor.Services;
using Microsoft.Extensions.Options;

namespace ProjectPulse.Processor.ContainerApp;

public sealed class ClaimPollingWorker(
    IHealthGate healthGate,
    IAppApiClient appApiClient,
    IClaimProcessor claimProcessor,
    IPollBatchLedger pollBatchLedger,
    IOptions<ClaimProcessorOptions> claimProcessorOptions,
    ApimOutageTracker outageTracker,
    IClaimPollingTrigger pollTrigger,
    TelemetryClient telemetry,
    ILogger<ClaimPollingWorker> logger) : BackgroundService
{
    private static readonly TimeSpan DuplicateLookback = TimeSpan.FromMinutes(15);
    private const int MaxTrackedRtaIdentities = 25_000;
    private readonly Dictionary<string, DuplicateObservation> recentRtaIdentities = new(StringComparer.OrdinalIgnoreCase);
    private static TimeSpan GetPollingInterval()
    {
        var raw = Environment.GetEnvironmentVariable("POLL_INTERVAL_SECONDS");
        return int.TryParse(raw, out var seconds)
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 300))
            : TimeSpan.FromSeconds(5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = GetPollingInterval();

        logger.LogInformation(
            "Project Pulse Container App polling worker started. PollIntervalSeconds={PollIntervalSeconds}",
            interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            var manuallyRequested = pollTrigger.ConsumePendingPoll();
            if (manuallyRequested)
            {
                logger.LogInformation("Starting queued manual RTA poll");
            }

            var started = DateTimeOffset.UtcNow;

            try
            {
                await RunPollAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error in claims polling loop");
                telemetry.TrackException(ex);
            }

            var elapsed = DateTimeOffset.UtcNow - started;
            var remaining = interval - elapsed;

            if (remaining > TimeSpan.Zero)
            {
                try
                {
                    var manualPollReceived = await pollTrigger.WaitForManualPollAsync(remaining, stoppingToken);
                    if (manualPollReceived)
                    {
                        logger.LogInformation("Manual RTA poll requested; starting the next poll cycle");
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        logger.LogInformation("Project Pulse Container App polling worker stopped");
    }

    private async Task RunPollAsync(CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid().ToString("N");
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["PollRunId"] = runId, ["WorkerInstance"] = Environment.MachineName });

        logger.LogInformation("RTA queued request poll started");

        var (rtaHealth, apimHealth) = await healthGate.CheckAsync(cancellationToken);

        if (!rtaHealth.IsHealthy)
        {
            telemetry.TrackEvent("ClaimPollSkippedRtaUnhealthy", new Dictionary<string, string>
            {
                ["runId"] = runId
            });

            logger.LogWarning("Poll skipped because RTA API/DB health is unhealthy");
            return;
        }

        var outageDecision = outageTracker.Evaluate(apimHealth.IsHealthy);

        if (outageDecision.OutageStarted)
        {
            logger.LogWarning(
                "APIM outage detected. Mode decision={Action}. Temporary buffering window started",
                outageDecision.Action);
        }

        if (outageDecision.Recovered)
        {
            logger.LogInformation("APIM recovered. Normal RTA polling and APIM delivery resumed");
            telemetry.TrackEvent("ApimRecovered");
        }

        if (outageDecision.Action == ApimOutageAction.StopPolling)
        {
            telemetry.TrackEvent("ClaimPollSuspendedForApimOutage", new Dictionary<string, string>
            {
                ["runId"] = runId,
                ["outageSeconds"] = Math.Round(outageDecision.OutageSeconds).ToString(),
                ["reason"] = outageDecision.Reason
            });

            logger.LogWarning(
                "RTA polling suspended. APIM outage duration={OutageSeconds:F0}s Reason={Reason}",
                outageDecision.OutageSeconds,
                outageDecision.Reason);

            return;
        }

        AppApiPollResult pollResult;
        try
        {
            pollResult = await appApiClient.GetPendingClaimsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            logger.LogError(ex, "RTA queued polling failed before records could be processed");
            telemetry.TrackException(ex);
            return;
        }

        var claims = pollResult.Claims;
        if (claims.Count == 0)
        {
            logger.LogInformation("No queued requests returned by RTA/App API");
            return;
        }

        logger.LogInformation(
            "RTA/App API returned {Count} queued record(s). DeliveryMode={DeliveryMode}",
            claims.Count,
            outageDecision.Action == ApimOutageAction.BufferToServiceBus ? "ServiceBusBuffer" : "APIM");

        var receivedAtUtc = pollResult.RecordsReceivedAtUtc ?? DateTimeOffset.UtcNow;
        try
        {
            var manifest = claims.Select((claim, ordinal) => new PollBatchRecordSeed(
                ordinal,
                GetRecordIdentifier(claim),
                TryGetScalar(claim, "REQUEST_ID", out var requestId) ? requestId : null)).ToArray();
            await pollBatchLedger.RecordReceivedAsync(
                runId,
                claimProcessorOptions.Value.IsMockApiMode() ? "MOCK" : "RTA",
                receivedAtUtc,
                pollResult.RtaPullDurationMs,
                claimProcessorOptions.Value.RtaStatusWritebackEnabled,
                manifest,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not persist poll batch manifest for PollRunId={PollRunId}", runId);
        }

        LogSuspectedDuplicateRtaRecords(claims, runId);

        IReadOnlyList<ClaimProcessingResult> results;
        try
        {
            results = outageDecision.Action == ApimOutageAction.BufferToServiceBus
                ? await claimProcessor.BufferBatchAsync(
                    claims,
                    outageDecision.Reason,
                    pollResult.RtaResponsePayloads,
                    cancellationToken,
                    runId)
                : await claimProcessor.ProcessBatchAsync(
                    claims,
                    pollResult.RtaResponsePayloads,
                    cancellationToken,
                    pollResult.RecordsReceivedAtUtc,
                    pollResult.RtaPullDurationMs,
                    pollRunId: runId);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Batch processing failed. If APIM is unavailable and Service Bus buffering also failed, polling will remain suspended until APIM recovers");

            telemetry.TrackException(ex);
            return;
        }
        finally
        {
            try
            {
                await pollBatchLedger.CompletePollAsync(runId, DateTimeOffset.UtcNow, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not finalize poll batch ledger for PollRunId={PollRunId}", runId);
            }
        }

        var summary = results
            .GroupBy(x => x.Disposition)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        telemetry.TrackEvent("ClaimPollCompleted", summary.ToDictionary(x => x.Key, x => x.Value.ToString()));
        telemetry.TrackMetric("ClaimBatchSize", claims.Count);

        logger.LogInformation(
            "Poll completed. Batched={Batched}, Completed={Completed}, RetryBuffered={Retry}, WritebackRetryQueued={WritebackRetryQueued}, Alerted={Alerted}, WritebackFailed={WritebackFailed}",
            claims.Count,
            summary.GetValueOrDefault(nameof(ClaimProcessingDisposition.Completed), 0),
            summary.GetValueOrDefault(nameof(ClaimProcessingDisposition.QueuedForRetry), 0),
            summary.GetValueOrDefault(nameof(ClaimProcessingDisposition.QueuedForWritebackRetry), 0),
            summary.GetValueOrDefault(nameof(ClaimProcessingDisposition.Alerted), 0),
            summary.GetValueOrDefault(nameof(ClaimProcessingDisposition.FailedWriteback), 0));
    }

    private void LogSuspectedDuplicateRtaRecords(IReadOnlyList<JsonElement> claims, string pollRunId)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var expiredKey in recentRtaIdentities
                     .Where(x => now - x.Value.LastSeenUtc > DuplicateLookback)
                     .Select(x => x.Key)
                     .ToArray())
        {
            recentRtaIdentities.Remove(expiredKey);
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var claim in claims)
        {
            foreach (var (kind, value) in GetRtaIdentityValues(claim))
            {
                var key = $"{kind}:{value}";
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        foreach (var (key, count) in counts.Where(x => x.Value > 1))
        {
            var separator = key.IndexOf(':');
            var kind = key[..separator];
            var value = key[(separator + 1)..];
            logger.LogWarning(
                "Suspected duplicate RTA record in one poll batch. IdentifierType={IdentifierType}, Identifier={Identifier}, Occurrences={Occurrences}, PollRunId={PollRunId}",
                kind,
                value,
                count,
                pollRunId);
            telemetry.TrackEvent("SuspectedDuplicateRtaRecord", new Dictionary<string, string>
            {
                ["identifierType"] = kind,
                ["identifier"] = value,
                ["occurrences"] = count.ToString(),
                ["scope"] = "same-poll",
                ["pollRunId"] = pollRunId,
                ["workerInstance"] = Environment.MachineName
            });
        }

        foreach (var (key, count) in counts)
        {
            if (recentRtaIdentities.TryGetValue(key, out var previous) &&
                previous.PollRunId != pollRunId)
            {
                var separator = key.IndexOf(':');
                var kind = key[..separator];
                var value = key[(separator + 1)..];
                logger.LogWarning(
                    "Suspected RTA record returned in a later poll within the duplicate lookback window. IdentifierType={IdentifierType}, Identifier={Identifier}, PreviousPollRunId={PreviousPollRunId}, PollRunId={PollRunId}, SecondsSincePreviousSeen={SecondsSincePreviousSeen:F1}",
                    kind,
                    value,
                    previous.PollRunId,
                    pollRunId,
                    (now - previous.LastSeenUtc).TotalSeconds);
                telemetry.TrackEvent("SuspectedDuplicateRtaRecord", new Dictionary<string, string>
                {
                    ["identifierType"] = kind,
                    ["identifier"] = value,
                    ["occurrences"] = count.ToString(),
                    ["scope"] = "later-poll",
                    ["previousPollRunId"] = previous.PollRunId,
                    ["pollRunId"] = pollRunId,
                    ["workerInstance"] = Environment.MachineName
                });
            }

            recentRtaIdentities[key] = new DuplicateObservation(now, pollRunId);
        }

        if (recentRtaIdentities.Count > MaxTrackedRtaIdentities)
        {
            foreach (var oldestKey in recentRtaIdentities
                         .OrderBy(x => x.Value.LastSeenUtc)
                         .Take(recentRtaIdentities.Count - MaxTrackedRtaIdentities)
                         .Select(x => x.Key)
                         .ToArray())
            {
                recentRtaIdentities.Remove(oldestKey);
            }
        }
    }

    private static string GetRecordIdentifier(JsonElement claim)
    {
        if (claim.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in claim.EnumerateObject())
            {
                if (string.Equals(property.Name, "CORRELATION_ID", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(property.Name, "IDEMPOTENCY_KEY", StringComparison.OrdinalIgnoreCase))
                {
                    var value = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                        : property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.ToString();
                    if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
                }
            }
        }

        return "missing-id";
    }

    private static bool TryGetScalar(JsonElement claim, string fieldName, out string value)
    {
        value = string.Empty;
        if (claim.ValueKind != JsonValueKind.Object ||
            !claim.TryGetProperty(fieldName, out var element))
        {
            return false;
        }

        value = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => element.ToString()
        };
        return !string.IsNullOrWhiteSpace(value);
    }

    private static IEnumerable<(string Kind, string Value)> GetRtaIdentityValues(JsonElement claim)
    {
        if (claim.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var property in claim.EnumerateObject())
        {
            if (!string.Equals(property.Name, "REQUEST_ID", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(property.Name, "CORRELATION_ID", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return (property.Name.ToUpperInvariant(), value.Trim());
            }
        }
    }

    private sealed record DuplicateObservation(DateTimeOffset LastSeenUtc, string PollRunId);
}
