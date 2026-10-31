using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectPulse.Processor.Models;
using ProjectPulse.Processor.Options;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProjectPulse.Processor.Services;

public sealed class ClaimProcessor(
    IHttpClientFactory httpClientFactory,
    IAuthHeaderProvider authHeaderProvider,
    IAppApiClient appApiClient,
    IRetryQueue retryQueue,
    IRtaNotificationClient rtaNotificationClient,
    IRequestHistoryStore requestHistoryStore,
    PayloadTransformer transformer,
    OutboundCircuitBreaker circuitBreaker,
    ApimOutageTracker outageTracker,
    IOptions<ClaimProcessorOptions> options,
    TelemetryClient telemetry,
    ILogger<ClaimProcessor> logger) : IClaimProcessor
{
    private readonly ClaimProcessorOptions _options = options.Value;
    private readonly HashSet<string> _jsonRetryCodes = options.Value.GetJsonRetryCodeSet();
    private readonly HashSet<string> _jsonAlertCodes = options.Value.GetJsonAlertCodeSet();

    public async Task<IReadOnlyList<ClaimProcessingResult>> BufferBatchAsync(
        IReadOnlyList<JsonElement> claims,
        string reason,
        IReadOnlyList<string> rtaResponsePayloads,
        CancellationToken cancellationToken)
    {
        if (claims.Count == 0)
        {
            return [];
        }

        var transformed = TransformClaims(claims);
        var ids = claims.Select(GetCorrelationId).ToArray();
        var rtaRequestIds = claims.Select(claim =>
            TryGetScalar(claim, "REQUEST_ID", out var requestId) ? requestId : null).ToArray();
        var batchId = Guid.NewGuid().ToString("N");

        try
        {
            await QueueAllAsync(claims, transformed, reason, cancellationToken);
        }
        catch (Exception ex)
        {
            outageTracker.RecordBufferFailure();
            await NotifyServiceBusFailureAsync(claims, reason, ex, cancellationToken);
            throw;
        }

        await TryRecordHistoryAsync(
            () => requestHistoryStore.RecordServiceBusAsync(
                batchId,
                ids,
                rtaRequestIds,
                rtaResponsePayloads,
                reason,
                DateTimeOffset.UtcNow,
                cancellationToken),
            "service bus buffer");

        telemetry.TrackEvent("ClaimBatchBufferedToServiceBus", new Dictionary<string, string>
        {
            ["recordCount"] = claims.Count.ToString(),
            ["reason"] = reason
        });

        logger.LogWarning(
            "Buffered {Count} RTA record(s) to Service Bus because APIM is unavailable. Reason={Reason}",
            claims.Count,
            reason);

        return BuildResults(ids, ClaimProcessingDisposition.QueuedForRetry, reason);
    }

    public async Task<IReadOnlyList<ClaimProcessingResult>> ProcessBatchAsync(
        IReadOnlyList<JsonElement> claims,
        IReadOnlyList<string> rtaResponsePayloads,
        CancellationToken cancellationToken,
        DateTimeOffset? pollStartedAtUtc = null,
        long? rtaPullDurationMs = null)
    {
        var mode = _options.GetProcessingConcurrencyMode();
        var maxConcurrency = mode == ProcessingConcurrencyMode.Sequential
            ? 1
            : _options.GetProcessingMaxConcurrency();
        var apimConcurrency = mode is ProcessingConcurrencyMode.ApimOnly or ProcessingConcurrencyMode.Full
            ? maxConcurrency
            : 1;
        var rtaConcurrency = mode is ProcessingConcurrencyMode.RtaOnly or ProcessingConcurrencyMode.Full
            ? maxConcurrency
            : 1;
        var resultsByClaim = new IReadOnlyList<ClaimProcessingResult>?[claims.Count];
        using var apimRequestGate = new SemaphoreSlim(apimConcurrency, apimConcurrency);
        using var rtaWritebackGate = new SemaphoreSlim(rtaConcurrency, rtaConcurrency);

        // Each record follows its own APIM/writeback path. DOCMP may omit correlation IDs,
        // so the single-item response is associated with its originating RTA request.
        // Separate gates make the selected concurrency mode explicit at each external boundary.
        await Parallel.ForEachAsync(
            Enumerable.Range(0, claims.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxConcurrency,
                CancellationToken = cancellationToken
            },
            async (index, itemCancellationToken) =>
            {
                resultsByClaim[index] = await ProcessSingleClaimAsync(
                    [claims[index]],
                    rtaResponsePayloads,
                    itemCancellationToken,
                    pollStartedAtUtc,
                    rtaPullDurationMs,
                    apimRequestGate,
                    rtaWritebackGate);
            });

        return resultsByClaim
            .Where(result => result is not null)
            .SelectMany(result => result!)
            .ToArray();
    }

    private async Task<IReadOnlyList<ClaimProcessingResult>> ProcessSingleClaimAsync(
        IReadOnlyList<JsonElement> claims,
        IReadOnlyList<string> rtaResponsePayloads,
        CancellationToken cancellationToken,
        DateTimeOffset? pollStartedAtUtc,
        long? rtaPullDurationMs,
        SemaphoreSlim apimRequestGate,
        SemaphoreSlim rtaWritebackGate)
    {
        if (claims.Count == 0)
        {
            return [];
        }

        var pipelineStartedAtUtc = pollStartedAtUtc ?? DateTimeOffset.UtcNow;
        var correlationIds = claims.Select(GetCorrelationId).ToArray();
        var rtaRequestIds = claims.Select(claim =>
            TryGetScalar(claim, "REQUEST_ID", out var requestId) ? requestId : null).ToArray();
        var translationStopwatch = Stopwatch.StartNew();
        var transformedClaims = TransformClaims(claims);
        translationStopwatch.Stop();
        var batchId = Guid.NewGuid().ToString("N");

        using var operation = telemetry.StartOperation<Microsoft.ApplicationInsights.DataContracts.RequestTelemetry>("ClaimBatchAdjudication");
        operation.Telemetry.Properties["batchId"] = batchId;
        operation.Telemetry.Properties["recordCount"] = claims.Count.ToString();
        var stopwatch = Stopwatch.StartNew();

        if (!circuitBreaker.CanAttempt())
        {
            try
            {
                await QueueAllAsync(claims, transformedClaims, "OutboundCircuitOpen", cancellationToken);
            }
            catch (Exception ex)
            {
                outageTracker.RecordBufferFailure();
                await NotifyServiceBusFailureAsync(claims, "OutboundCircuitOpen", ex, cancellationToken);
                throw;
            }

            await TryRecordHistoryAsync(
                () => requestHistoryStore.RecordServiceBusAsync(
                    batchId,
                    correlationIds,
                    rtaRequestIds,
                    rtaResponsePayloads,
                    "OutboundCircuitOpen",
                    DateTimeOffset.UtcNow,
                    cancellationToken),
                "circuit-open service bus buffer");

            CompleteTelemetry(operation.Telemetry, false, "CircuitOpen", stopwatch.ElapsedMilliseconds);
            return BuildResults(correlationIds, ClaimProcessingDisposition.QueuedForRetry, "OutboundCircuitOpen");
        }

        if (transformedClaims.Count != 1)
        {
            throw new InvalidOperationException("Each APIM request must contain exactly one mapped claim.");
        }

        var outboundPayloadJson = transformedClaims[0].ToJsonString();

        await TryRecordHistoryAsync(
            () => requestHistoryStore.RecordApimAttemptAsync(
                batchId,
                correlationIds,
                rtaRequestIds,
                rtaResponsePayloads,
                outboundPayloadJson,
                pipelineStartedAtUtc,
                rtaPullDurationMs,
                translationStopwatch.ElapsedMilliseconds,
                DateTimeOffset.UtcNow,
                cancellationToken),
            "APIM request");

        if (_options.ShouldCaptureRequestHistoryPayloads())
        {
            logger.LogInformation(
                "APIM outbound batch {BatchId} payload: {Payload}",
                batchId,
                _options.RedactRequestHistoryPayload(outboundPayloadJson));
        }

        var client = httpClientFactory.CreateClient("Apim");
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.ApimAdjudicationPath)
        {
            Content = new StringContent(outboundPayloadJson, Encoding.UTF8, "application/json")
        };

        request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationIds[0]);
        var idempotencyKey = TryGetScalar(claims[0], "IDEMPOTENCY_KEY", out var claimIdempotencyKey)
            ? claimIdempotencyKey
            : batchId;
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        if (!string.IsNullOrWhiteSpace(_options.ApimMockScenario))
        {
            request.Headers.TryAddWithoutValidation(
                "X-Mock-Scenario",
                _options.ApimMockScenario.Trim());
        }
        var apimRoundTripStopwatch = new Stopwatch();
        var apimRequestGateHeld = false;
        try
        {
            await apimRequestGate.WaitAsync(cancellationToken);
            apimRequestGateHeld = true;
            await authHeaderProvider.ApplyBearerTokenAsync(request, _options.ApimScope, cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.ApimRequestTimeoutSeconds));

            apimRoundTripStopwatch.Start();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token);
            var responseJson = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            apimRoundTripStopwatch.Stop();
            apimRequestGate.Release();
            apimRequestGateHeld = false;
            var apimResponseReceivedAtUtc = DateTimeOffset.UtcNow;
            var statusCode = (int)response.StatusCode;
            var hasApimError = TryParseApimError(responseJson, statusCode, out var apimError);
            var isDocmpErrorEnvelope = hasApimError && apimError.IsDocmpErrorEnvelope;

            // DOCMP error envelopes are application failures even when APIM mirrors their status
            // (for example, ACC-NOTF-001 with HTTP 404). Send those to RTA; only other non-200
            // transport responses use the HTTP retry/notification path.
            if (statusCode != 200 && !isDocmpErrorEnvelope)
            {
                circuitBreaker.RecordFailure();
                outageTracker.RecordApimFailure();
                var reason = $"Http{statusCode}";

                if (_options.GetRetryHttpStatusCodeSet().Contains(statusCode))
                {
                    try
                    {
                        await QueueAllAsync(claims, transformedClaims, reason, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        outageTracker.RecordBufferFailure();
                        await RecordBufferFailureHistoryAsync(
                            batchId,
                            correlationIds,
                            statusCode,
                            null,
                            responseJson,
                            $"ServiceBusBufferFailed:{reason}");
                        await NotifyApimHttpFailureAsync(
                            claims[0],
                            statusCode,
                            response.ReasonPhrase,
                            request.RequestUri?.ToString(),
                            apimRoundTripStopwatch.ElapsedMilliseconds,
                            serviceBusQueued: false,
                            serviceBusFailure: ex,
                            cancellationToken);
                        logger.LogError(
                            ex,
                            "APIM returned HTTP {StatusCode} for batch {BatchId}, but retry buffering failed. The APIM response was saved in request history.",
                            statusCode,
                            batchId);
                        throw;
                    }

                    await TryRecordHistoryAsync(
                        () => requestHistoryStore.RecordApimResultAsync(
                            batchId,
                            correlationIds,
                            "Buffered",
                            statusCode,
                            null,
                            responseJson,
                            reason,
                            true,
                            DateTimeOffset.UtcNow,
                            cancellationToken,
                            apimResponseReceivedAtUtc,
                            apimRoundTripStopwatch.ElapsedMilliseconds),
                        "retryable HTTP response");

                    await NotifyApimHttpFailureAsync(
                        claims[0],
                        statusCode,
                        response.ReasonPhrase,
                        request.RequestUri?.ToString(),
                        apimRoundTripStopwatch.ElapsedMilliseconds,
                        serviceBusQueued: true,
                        serviceBusFailure: null,
                        cancellationToken);

                    CompleteTelemetry(operation.Telemetry, false, $"HTTP {statusCode}", stopwatch.ElapsedMilliseconds);
                    return BuildResults(correlationIds, ClaimProcessingDisposition.QueuedForRetry, reason, statusCode);
                }

                logger.LogError(
                    "APIM batch {BatchId} with {Count} records received non-retry HTTP status {StatusCode}",
                    batchId,
                    claims.Count,
                    statusCode);

                await TryRecordHistoryAsync(
                    () => requestHistoryStore.RecordApimResultAsync(
                        batchId,
                        correlationIds,
                        "Alerted",
                        statusCode,
                        null,
                        responseJson,
                        reason,
                        false,
                        DateTimeOffset.UtcNow,
                        cancellationToken,
                        apimResponseReceivedAtUtc,
                        apimRoundTripStopwatch.ElapsedMilliseconds),
                    "non-retry HTTP response");

                await NotifyApimHttpFailureAsync(
                    claims[0],
                    statusCode,
                    response.ReasonPhrase,
                    request.RequestUri?.ToString(),
                    apimRoundTripStopwatch.ElapsedMilliseconds,
                    serviceBusQueued: false,
                    serviceBusFailure: null,
                    cancellationToken);

                CompleteTelemetry(operation.Telemetry, false, $"HTTP {statusCode}", stopwatch.ElapsedMilliseconds);
                return BuildResults(correlationIds, ClaimProcessingDisposition.Alerted, reason, statusCode);
            }

            if (!ValidateApimResponseToken(response, batchId))
            {
                await TryRecordHistoryAsync(
                    () => requestHistoryStore.RecordApimResultAsync(
                        batchId,
                        correlationIds,
                        "Rejected",
                        statusCode,
                        null,
                        responseJson,
                        "InvalidApimResponseToken",
                        false,
                        DateTimeOffset.UtcNow,
                        cancellationToken,

                        apimResponseReceivedAtUtc,

                        apimRoundTripStopwatch.ElapsedMilliseconds),
                    "APIM response-token rejection");

                telemetry.TrackEvent("ApimResponseValidationFailed", new Dictionary<string, string>
                {
                    ["batchId"] = batchId,
                    ["headerName"] = _options.ApimResponseHeaderName,
                    ["recordCount"] = claims.Count.ToString()
                });

                CompleteTelemetry(operation.Telemetry, false, "InvalidApimResponseToken", stopwatch.ElapsedMilliseconds);
                return BuildResults(
                    correlationIds,
                    ClaimProcessingDisposition.Alerted,
                    "InvalidApimResponseToken",
                    statusCode);
            }

            if (_options.ShouldCaptureRequestHistoryPayloads())
            {
                logger.LogInformation(
                    "APIM batch {BatchId} response. Status={StatusCode} Payload={Payload}",
                    batchId,
                    statusCode,
                    _options.RedactRequestHistoryPayload(responseJson));
            }

            if (hasApimError)
            {
                // DOCMP can return an error envelope in the JSON body while APIM itself
                // responds successfully or mirrors the DOCMP status. Treat it as a final
                // application error and report it to RTA.
                circuitBreaker.RecordSuccess();
                outageTracker.RecordApimSuccess();

                logger.LogWarning(
                    "APIM batch {BatchId} returned a JSON error envelope. HttpStatus={StatusCode}, Code={ErrorCode}",
                    batchId,
                    statusCode,
                    apimError.Code);

                var writebackSucceeded = false;
                var writebackReason = "RtaStatusWritebackDisabled";
                if (_options.RtaStatusWritebackEnabled)
                {
                    var writebackStopwatch = Stopwatch.StartNew();
                    var writebackResult = await WriteBatchResultsAsync(
                        responseJson,
                        statusCode,
                        apimResponseReceivedAtUtc,
                        apimRoundTripStopwatch.ElapsedMilliseconds,
                        request.RequestUri?.ToString() ?? new Uri(client.BaseAddress!, _options.ApimAdjudicationPath).ToString(),
                        SerializeApimResponseHeaders(response),
                        batchId,
                        claims,
                        correlationIds,
                        cancellationToken,
                        rtaWritebackGate,
                        apimError.Message,
                        apimError.Stack);
                    writebackStopwatch.Stop();
                    var writebackCompletedAtUtc = DateTimeOffset.UtcNow;

                    await TryRecordHistoryAsync(
                        () => requestHistoryStore.RecordWritebackTimingAsync(
                            batchId,
                            correlationIds,
                            writebackStopwatch.ElapsedMilliseconds,
                            writebackResult.Details,
                            writebackCompletedAtUtc,
                            cancellationToken),
                        "RTA error writeback timing");

                    writebackSucceeded = writebackResult.Success;
                    writebackReason = writebackResult.Reason;
                }

                var historyStatus = writebackSucceeded || !_options.RtaStatusWritebackEnabled
                    ? "Alerted"
                    : "WritebackFailed";
                var errorReason = $"ApimJsonError:{apimError.Code ?? "Unknown"}";
                await TryRecordHistoryAsync(
                    () => requestHistoryStore.RecordApimResultAsync(
                        batchId,
                        correlationIds,
                        historyStatus,
                        statusCode,
                        apimError.Code,
                        responseJson,
                        writebackSucceeded || !_options.RtaStatusWritebackEnabled
                            ? errorReason
                            : $"{errorReason};{writebackReason}",
                        false,
                        DateTimeOffset.UtcNow,
                        cancellationToken,
                        apimResponseReceivedAtUtc,
                        apimRoundTripStopwatch.ElapsedMilliseconds),
                    "structured APIM error response");

                CompleteTelemetry(operation.Telemetry, false, errorReason, stopwatch.ElapsedMilliseconds);
                return BuildResults(
                    correlationIds,
                    writebackSucceeded || !_options.RtaStatusWritebackEnabled
                        ? ClaimProcessingDisposition.Alerted
                        : ClaimProcessingDisposition.FailedWriteback,
                    errorReason,
                    statusCode,
                    apimError.Code);
            }

            circuitBreaker.RecordSuccess();
            outageTracker.RecordApimSuccess();
            var businessCode = TryGetBusinessCode(responseJson);

            if (!string.IsNullOrWhiteSpace(businessCode) && _jsonRetryCodes.Contains(businessCode))
            {
                try
                {
                    await QueueAllAsync(claims, transformedClaims, $"JsonCode:{businessCode}", cancellationToken);
                }
                catch (Exception ex)
                {
                    outageTracker.RecordBufferFailure();
                    await RecordBufferFailureHistoryAsync(
                        batchId,
                        correlationIds,
                        statusCode,
                        businessCode,
                        responseJson,
                        $"ServiceBusBufferFailed:JsonCode:{businessCode}");
                    await NotifyServiceBusFailureAsync(
                        claims,
                        $"JsonCode:{businessCode}",
                        ex,
                        cancellationToken);
                    logger.LogError(
                        ex,
                        "APIM response for batch {BatchId} had retryable business code {BusinessCode}, but retry buffering failed. The APIM response was saved in request history.",
                        batchId,
                        businessCode);
                    throw;
                }

                await TryRecordHistoryAsync(
                    () => requestHistoryStore.RecordApimResultAsync(
                        batchId,
                        correlationIds,
                        "Buffered",
                        statusCode,
                        businessCode,
                        responseJson,
                        $"JsonCode:{businessCode}",
                        true,
                        DateTimeOffset.UtcNow,
                        cancellationToken,

                        apimResponseReceivedAtUtc,

                        apimRoundTripStopwatch.ElapsedMilliseconds),
                    "JSON retry response");

                CompleteTelemetry(operation.Telemetry, false, $"JSON {businessCode}", stopwatch.ElapsedMilliseconds);
                return BuildResults(correlationIds, ClaimProcessingDisposition.QueuedForRetry, "JsonRetryCode", statusCode, businessCode);
            }

            if (_options.RtaStatusWritebackEnabled)
            {
                var writebackStopwatch = Stopwatch.StartNew();
                var writebackResult = await WriteBatchResultsAsync(
                    responseJson,
                    statusCode,
                    apimResponseReceivedAtUtc,
                    apimRoundTripStopwatch.ElapsedMilliseconds,
                    request.RequestUri?.ToString() ?? new Uri(client.BaseAddress!, _options.ApimAdjudicationPath).ToString(),
                    SerializeApimResponseHeaders(response),
                    batchId,
                    claims,
                    correlationIds,
                    cancellationToken,
                    rtaWritebackGate);
                writebackStopwatch.Stop();
                var writebackCompletedAtUtc = DateTimeOffset.UtcNow;
                await TryRecordHistoryAsync(
                    () => requestHistoryStore.RecordWritebackTimingAsync(
                        batchId,
                        correlationIds,
                        writebackStopwatch.ElapsedMilliseconds,
                        writebackResult.Details,
                        writebackCompletedAtUtc,
                        cancellationToken),
                    "RTA writeback timing");

                if (!writebackResult.Success && writebackResult.Reason == "RtaWritebackQueued")
                {
                    await TryRecordHistoryAsync(
                        () => requestHistoryStore.RecordApimResultAsync(
                            batchId,
                            correlationIds,
                            "Buffered",
                            statusCode,
                            businessCode,
                            responseJson,
                            "RtaWritebackQueued",
                            true,
                            DateTimeOffset.UtcNow,
                            cancellationToken,
                            apimResponseReceivedAtUtc,
                            apimRoundTripStopwatch.ElapsedMilliseconds),
                        "RTA writeback retry buffer");

                    CompleteTelemetry(operation.Telemetry, true, "RtaWritebackQueued", stopwatch.ElapsedMilliseconds);
                    return BuildResults(
                        correlationIds,
                        ClaimProcessingDisposition.QueuedForWritebackRetry,
                        "RtaWritebackQueued",
                        statusCode,
                        businessCode);
                }

                if (!writebackResult.Success)
                {
                    await TryRecordHistoryAsync(
                        () => requestHistoryStore.RecordApimResultAsync(
                            batchId,
                            correlationIds,
                            "WritebackFailed",
                            statusCode,
                            businessCode,
                            responseJson,
                            writebackResult.Reason,
                            false,
                            DateTimeOffset.UtcNow,
                            cancellationToken,

                            apimResponseReceivedAtUtc,

                            apimRoundTripStopwatch.ElapsedMilliseconds),
                        "writeback failure");

                    CompleteTelemetry(operation.Telemetry, false, writebackResult.Reason, stopwatch.ElapsedMilliseconds);
                    return BuildResults(
                        correlationIds,
                        ClaimProcessingDisposition.FailedWriteback,
                        writebackResult.Reason,
                        statusCode,
                        businessCode);
                }
            }
            else
            {
                telemetry.TrackEvent("RtaStatusWritebackSkipped", new Dictionary<string, string>
                {
                    ["batchId"] = batchId,
                    ["recordCount"] = claims.Count.ToString()
                });
            }

            if (!string.IsNullOrWhiteSpace(businessCode) && _jsonAlertCodes.Contains(businessCode))
            {
                telemetry.TrackEvent("ClaimBatchBusinessAlert", new Dictionary<string, string>
                {
                    ["batchId"] = batchId,
                    ["businessCode"] = businessCode,
                    ["recordCount"] = claims.Count.ToString()
                });

                await TryRecordHistoryAsync(
                    () => requestHistoryStore.RecordApimResultAsync(
                        batchId,
                        correlationIds,
                        "Alerted",
                        statusCode,
                        businessCode,
                        responseJson,
                        "JsonAlertCode",
                        false,
                        DateTimeOffset.UtcNow,
                        cancellationToken,

                        apimResponseReceivedAtUtc,

                        apimRoundTripStopwatch.ElapsedMilliseconds),
                    "JSON alert response");

                CompleteTelemetry(operation.Telemetry, true, $"JSON alert {businessCode}", stopwatch.ElapsedMilliseconds);
                return BuildResults(correlationIds, ClaimProcessingDisposition.Alerted, "JsonAlertCode", statusCode, businessCode);
            }

            await TryRecordHistoryAsync(
                () => requestHistoryStore.RecordApimResultAsync(
                    batchId,
                    correlationIds,
                    "Completed",
                    statusCode,
                    businessCode,
                    responseJson,
                    "BatchCompleted",
                    false,
                    DateTimeOffset.UtcNow,
                    cancellationToken,

                    apimResponseReceivedAtUtc,

                    apimRoundTripStopwatch.ElapsedMilliseconds),
                "successful APIM response");

            CompleteTelemetry(operation.Telemetry, true, "Completed", stopwatch.ElapsedMilliseconds);
            return BuildResults(correlationIds, ClaimProcessingDisposition.Completed, "BatchCompleted", statusCode, businessCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            circuitBreaker.RecordFailure();
            outageTracker.RecordApimFailure();

            try
            {
                await QueueAllAsync(claims, transformedClaims, "ApimTimeout", cancellationToken);
            }
            catch (Exception queueException)
            {
                outageTracker.RecordBufferFailure();
                await NotifyServiceBusFailureAsync(claims, "ApimTimeout", queueException, cancellationToken);
                throw;
            }

            await TryRecordHistoryAsync(
                () => requestHistoryStore.RecordApimResultAsync(
                    batchId,
                    correlationIds,
                    "Buffered",
                    null,
                    null,
                    null,
                    "ApimTimeout",
                    true,
                    DateTimeOffset.UtcNow,
                    cancellationToken,
                    null,
                    apimRoundTripStopwatch.ElapsedMilliseconds),
                "APIM timeout");

            CompleteTelemetry(operation.Telemetry, false, "Timeout", stopwatch.ElapsedMilliseconds);
            return BuildResults(correlationIds, ClaimProcessingDisposition.QueuedForRetry, "ApimTimeout");
        }
        catch (HttpRequestException ex)
        {
            circuitBreaker.RecordFailure();
            outageTracker.RecordApimFailure();
            var statusCode = ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : (int?)null;

            try
            {
                await QueueAllAsync(claims, transformedClaims, "NetworkFailure", cancellationToken);
            }
            catch (Exception queueException)
            {
                outageTracker.RecordBufferFailure();
                await NotifyServiceBusFailureAsync(claims, "NetworkFailure", queueException, cancellationToken);
                throw;
            }

            await TryRecordHistoryAsync(
                () => requestHistoryStore.RecordApimResultAsync(
                    batchId,
                    correlationIds,
                    "Buffered",
                    statusCode,
                    null,
                    null,
                    "NetworkFailure",
                    true,
                    DateTimeOffset.UtcNow,
                    cancellationToken,
                    null,
                    apimRoundTripStopwatch.ElapsedMilliseconds),
                "APIM network failure");

            logger.LogWarning(ex, "Network failure while processing APIM batch {BatchId}", batchId);
            CompleteTelemetry(operation.Telemetry, false, "NetworkFailure", stopwatch.ElapsedMilliseconds);
            return BuildResults(correlationIds, ClaimProcessingDisposition.QueuedForRetry, "NetworkFailure", statusCode);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Invalid JSON response while processing APIM batch {BatchId}", batchId);
            telemetry.TrackException(ex);
            CompleteTelemetry(operation.Telemetry, false, "InvalidJson", stopwatch.ElapsedMilliseconds);
            return BuildResults(correlationIds, ClaimProcessingDisposition.Alerted, "InvalidJsonResponse");
        }
        finally
        {
            if (apimRoundTripStopwatch.IsRunning)
            {
                apimRoundTripStopwatch.Stop();
            }

            if (apimRequestGateHeld)
            {
                apimRequestGate.Release();
            }
        }
    }

    private async Task RecordBufferFailureHistoryAsync(
        string batchId,
        IReadOnlyList<string> correlationIds,
        int? httpStatusCode,
        string? businessCode,
        string? responseJson,
        string reason)
    {
        await TryRecordHistoryAsync(
            () => requestHistoryStore.RecordApimResultAsync(
                batchId,
                correlationIds,
                "BufferFailed",
                httpStatusCode,
                businessCode,
                responseJson,
                reason,
                false,
                DateTimeOffset.UtcNow,
                CancellationToken.None),
            "APIM response after Service Bus buffering failure");
    }

    private async Task TryRecordHistoryAsync(Func<Task> action, string operation)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Request history write failed during {Operation}", operation);
        }
    }

    private IReadOnlyList<JsonObject> TransformClaims(IReadOnlyList<JsonElement> claims) =>
        claims.Select(transformer.Transform).ToArray();

    private string GetCorrelationId(JsonElement claim)
    {
        if (TryGetScalar(claim, _options.CorrelationIdField, out var configured))
        {
            return configured;
        }

        if (_options.IsMockApiMode())
        {
            if (TryGetScalar(claim, "uniqueId", out var mockId) ||
                TryGetScalar(claim, "UniqueId", out mockId))
            {
                return mockId;
            }
        }

        if (TryGetScalar(claim, "CORRELATION_ID", out var rtaId))
        {
            return rtaId;
        }

        throw new InvalidOperationException(
            $"Queued request JSON does not contain configured correlation ID field '{_options.CorrelationIdField}'.");
    }

    private async Task NotifyApimHttpFailureAsync(
        JsonElement claim,
        int statusCode,
        string? reasonPhrase,
        string? apimUrl,
        long responseTimeMs,
        bool serviceBusQueued,
        Exception? serviceBusFailure,
        CancellationToken cancellationToken)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HTTP_STATUS_CODE"] = statusCode.ToString(),
            ["HTTP_REASON_PHRASE"] = reasonPhrase ?? string.Empty,
            ["APIM_URL"] = apimUrl ?? string.Empty,
            ["RESPONSE_TIME_MS"] = responseTimeMs.ToString(),
            ["SERVICE_BUS_QUEUE"] = _options.ServiceBusQueueName,
            ["SERVICE_BUS_QUEUED"] = serviceBusQueued.ToString()
        };

        if (serviceBusFailure is not null)
        {
            attributes["SERVICE_BUS_FAILURE_TYPE"] = serviceBusFailure.GetType().Name;
            attributes["SERVICE_BUS_FAILURE_MESSAGE"] = serviceBusFailure.Message;
        }

        var body = serviceBusFailure is null
            ? $"Project Pulse received HTTP {statusCode} from APIM. The request {(serviceBusQueued ? "was queued for retry" : "was not queued because this status is not configured for retry")}."
            : $"Project Pulse received HTTP {statusCode} from APIM and could not queue the request to Service Bus. Service Bus error: {serviceBusFailure.GetType().Name}: {serviceBusFailure.Message}";

        await rtaNotificationClient.SendFailureAsync(
            claim,
            "APIM",
            "HTTP_STATUS",
            body,
            attributes,
            cancellationToken);
    }

    private async Task NotifyServiceBusFailureAsync(
        IReadOnlyList<JsonElement> claims,
        string reason,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SERVICE_BUS_QUEUE"] = _options.ServiceBusQueueName,
            ["SERVICE_BUS_FAILURE_TYPE"] = exception.GetType().Name,
            ["SERVICE_BUS_FAILURE_MESSAGE"] = exception.Message,
            ["QUEUE_REASON"] = reason
        };

        foreach (var claim in claims)
        {
            await rtaNotificationClient.SendFailureAsync(
                claim,
                "SERVICE_BUS",
                "SEND_FAILURE",
                $"Project Pulse could not send the request to Service Bus for retry. Reason: {reason}. Error: {exception.GetType().Name}: {exception.Message}",
                attributes,
                cancellationToken);
        }
    }

    private async Task QueueAllAsync(
        IReadOnlyList<JsonElement> claims,
        IReadOnlyList<JsonObject> transformedClaims,
        string reason,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < claims.Count; i++)
        {
            var claim = claims[i];
            var uniqueId = GetCorrelationId(claim);
            TryGetScalar(claim, "REQUEST_ID", out var requestId);
            TryGetScalar(claim, "IDEMPOTENCY_KEY", out var idempotencyKey);

            var envelope = new RetryEnvelope(
                uniqueId,
                string.IsNullOrWhiteSpace(requestId) ? null : requestId,
                string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey,
                reason,
                DateTimeOffset.UtcNow,
                claim.GetRawText(),
                transformedClaims[i].ToJsonString());

            await retryQueue.EnqueueAsync(envelope, cancellationToken);
        }
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

    private bool ValidateApimResponseToken(HttpResponseMessage response, string batchId)
    {
        if (string.IsNullOrWhiteSpace(_options.ApimResponseToken))
        {
            logger.LogError(
                "APIM response validation failed for batch {BatchId}: ApimResponseToken is not configured.",
                batchId);
            return false;
        }

        if (!response.Headers.TryGetValues(_options.ApimResponseHeaderName, out var values))
        {
            logger.LogError(
                "APIM response validation failed for batch {BatchId}: required header {HeaderName} was missing.",
                batchId,
                _options.ApimResponseHeaderName);
            return false;
        }

        var actual = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(actual))
        {
            logger.LogError(
                "APIM response validation failed for batch {BatchId}: required header {HeaderName} was empty.",
                batchId,
                _options.ApimResponseHeaderName);
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(_options.ApimResponseToken);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        var valid = expectedBytes.Length == actualBytes.Length &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);

        if (!valid)
        {
            logger.LogError(
                "APIM response validation failed for batch {BatchId}: header {HeaderName} did not match the configured token.",
                batchId,
                _options.ApimResponseHeaderName);
            return false;
        }

        logger.LogInformation(
            "APIM response validation succeeded for batch {BatchId}. Header {HeaderName} was present and matched the configured token.",
            batchId,
            _options.ApimResponseHeaderName);

        telemetry.TrackEvent("ApimResponseValidationSucceeded", new Dictionary<string, string>
        {
            ["batchId"] = batchId,
            ["headerName"] = _options.ApimResponseHeaderName
        });

        return true;
    }

    private string SerializeApimResponseHeaders(HttpResponseMessage response)
    {
        var headers = response.Headers.Concat(response.Content.Headers);
        return string.Join(
            "\r\n",
            headers.Select(header =>
            {
                var value = string.Equals(
                    header.Key,
                    _options.ApimResponseHeaderName,
                    StringComparison.OrdinalIgnoreCase)
                    ? "********"
                    : string.Join(", ", header.Value);
                return $"{header.Key}: {value}";
            }));
    }

    private async Task<(bool Success, string Reason, RtaWritebackResult? Details)> WriteBatchResultsAsync(
        string responseJson,
        int apimStatusCode,
        DateTimeOffset apimResponseReceivedAtUtc,
        long apimResponseTimeMs,
        string apimRequestUrl,
        string apimResponseHeaders,
        string batchId,
        IReadOnlyList<JsonElement> claims,
        IReadOnlyList<string> correlationIds,
        CancellationToken cancellationToken,
        SemaphoreSlim rtaWritebackGate,
        string? apimErrorMessage = null,
        string? apimErrorStack = null)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            logger.LogWarning("RTA writeback is enabled but APIM returned an empty response body.");
            return (false, "EmptyApimResponse", null);
        }

        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        JsonElement[] responseItems = root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray().ToArray(),
            JsonValueKind.Object when correlationIds.Count == 1 => [root],
            _ => []
        };

        if (responseItems.Length == 0)
        {
            logger.LogWarning(
                "RTA writeback is enabled but APIM response was not a supported object or array.");
            return (false, "InvalidApimResponseShape", null);
        }

        if (responseItems.Length != correlationIds.Count || claims.Count != correlationIds.Count)
        {
            logger.LogWarning(
                "APIM response item count {ResponseCount}, request count {RequestCount}, and submitted count {SubmittedCount} did not match",
                responseItems.Length,
                claims.Count,
                correlationIds.Count);
            return (false, "ApimResponseCountMismatch", null);
        }

        var requestsByCorrelationId = claims.ToDictionary(
            GetCorrelationId,
            claim => claim,
            StringComparer.OrdinalIgnoreCase);
        var expectedCorrelationIds = correlationIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var responseCorrelationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchedResponses = new List<(JsonElement Response, string CorrelationId)>(responseItems.Length);
        RtaWritebackResult? rtaWritebackResult = null;

        foreach (var item in responseItems)
        {
            if (TryGetResponseCorrelationId(item, out var responseCorrelationId))
            {
                if (!expectedCorrelationIds.Contains(responseCorrelationId))
                {
                    logger.LogError(
                        "APIM response contained unexpected correlation ID {CorrelationId}. Writeback rejected.",
                        responseCorrelationId);
                    return (false, "UnexpectedCorrelationId", null);
                }

                if (!responseCorrelationIds.Add(responseCorrelationId))
                {
                    logger.LogError(
                        "APIM response contained duplicate correlation ID {CorrelationId}. Writeback rejected.",
                        responseCorrelationId);
                    return (false, "DuplicateCorrelationId", null);
                }

                matchedResponses.Add((item, responseCorrelationId));
                continue;
            }

            // DOCMP returns a single business response without RTA correlation fields.
            // The one-to-one request flow provides the association in that case.
            if (responseItems.Length != 1 || correlationIds.Count != 1)
            {
                logger.LogWarning("APIM response item did not contain a correlation ID. Writeback rejected.");
                return (false, "MissingCorrelationId", null);
            }

            matchedResponses.Add((item, correlationIds[0]));
            responseCorrelationIds.Add(correlationIds[0]);
        }

        if (!expectedCorrelationIds.SetEquals(responseCorrelationIds))
        {
            logger.LogError("APIM response correlation IDs did not exactly match the submitted batch. Writeback rejected.");
            return (false, "CorrelationIdSetMismatch", null);
        }

        var retryQueued = false;
        foreach (var (item, correlationId) in matchedResponses)
        {
            var originatingRequest = requestsByCorrelationId[correlationId];
            var requestType = GetScalar(originatingRequest, "REQUEST_TYPE");
            var isInitialRequest = string.Equals(requestType, "INITIAL_REQUEST", StringComparison.OrdinalIgnoreCase);
            var statusPayload = apimErrorMessage is not null
                ? BuildInitialRtaStatusPayload(
                    item,
                    originatingRequest,
                    responseJson,
                    apimStatusCode,
                    apimResponseReceivedAtUtc,
                    apimResponseTimeMs,
                    _options.ApimBaseUrl,
                    _options.ApimAdjudicationPath,
                    apimResponseHeaders,
                    apimErrorMessage,
                    apimErrorStack,
                    isErrorResponse: true)
                : isInitialRequest
                    ? BuildInitialRtaStatusPayload(
                        item,
                        originatingRequest,
                        responseJson,
                        apimStatusCode,
                        apimResponseReceivedAtUtc,
                        apimResponseTimeMs,
                        _options.ApimBaseUrl,
                        _options.ApimAdjudicationPath,
                        apimResponseHeaders)
                    : BuildLegacyRtaStatusPayload(item, originatingRequest);
            var statusPayloadJson = statusPayload.ToJsonString();

            await TryRecordHistoryAsync(
                () => requestHistoryStore.RecordRtaStatusPayloadAsync(
                    batchId,
                    [correlationId],
                    [statusPayloadJson],
                    cancellationToken),
                "RTA status payload");
            await rtaWritebackGate.WaitAsync(cancellationToken);
            try
            {
                rtaWritebackResult = await appApiClient.WriteStatusAsync(
                    correlationId,
                    statusPayloadJson,
                    cancellationToken);
            }
            finally
            {
                rtaWritebackGate.Release();
            }

            if (rtaWritebackResult.Succeeded != true)
            {
                logger.LogError(
                    "RTA status writeback failed for correlation ID {CorrelationId}; queueing the exact translated RTA payload for direct retry.",
                    correlationId);

                var queuedAtUtc = DateTimeOffset.UtcNow;
                var envelope = new RtaWritebackRetryEnvelope(
                    RtaWritebackRetryEnvelope.TypeName,
                    correlationId,
                    GetScalar(originatingRequest, "REQUEST_ID"),
                    GetScalar(originatingRequest, "IDEMPOTENCY_KEY"),
                    "RtaStatusWritebackFailed",
                    queuedAtUtc,
                    queuedAtUtc.AddMinutes(_options.ServiceBusMessageTtlMinutes),
                    0,
                    correlationId,
                    statusPayloadJson);

                try
                {
                    await retryQueue.EnqueueRtaWritebackAsync(envelope, cancellationToken);
                    retryQueued = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await NotifyServiceBusFailureAsync(
                        [originatingRequest],
                        "RtaWritebackRetry",
                        ex,
                        cancellationToken);
                    return (false, "RtaWritebackRetryEnqueueFailed", rtaWritebackResult);
                }
            }
        }

        return retryQueued
            ? (false, "RtaWritebackQueued", rtaWritebackResult)
            : (true, "WritebackCompleted", rtaWritebackResult);
    }

    private static JsonObject BuildInitialRtaStatusPayload(
        JsonElement apimResponse,
        JsonElement originatingRequest,
        string rawApimResponse,
        int apimStatusCode,
        DateTimeOffset apimResponseReceivedAtUtc,
        long apimResponseTimeMs,
        string apimBaseUrl,
        string apimEndpoint,
        string apimResponseHeaders,
        string? errorMessage = null,
        string? errorStack = null,
        bool isErrorResponse = false)
    {
        var payload = new JsonObject();
        CopyJsonProperty(originatingRequest, payload, "REQUEST_TYPE");
        payload["STATUS"] = isErrorResponse
            ? "FAILURE"
            : apimStatusCode is >= 200 and < 300 ? "SUCCESS" : "FAILURE";
        payload["SUBSTATUS"] = null;
        payload["PROCESS_DT"] = apimResponseReceivedAtUtc
            .ToUniversalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);

        // Preserve the RTA API_CALL_HISTORY field names exactly; REQUEST_HEADER is the
        // contract name used here for the captured APIM response headers.
        var apiCallHistory = new JsonObject();
        CopyJsonProperty(originatingRequest, apiCallHistory, "REQUEST_ID");
        CopyJsonProperty(originatingRequest, apiCallHistory, "IDEMPOTENCY_KEY");
        apiCallHistory["EXTERNAL_CORRELATION_ID"] = null;
        CopyJsonProperty(originatingRequest, apiCallHistory, "CORRELATION_ID");
        apiCallHistory["IDENTIFIER"] = "initial-accum-request";
        apiCallHistory["HTTP_METHOD"] = "POST";
        apiCallHistory["API_URL"] = $"{apimBaseUrl.TrimEnd('/')}/";
        apiCallHistory["API_ENDPOINT"] = apimEndpoint.Trim('/');
        apiCallHistory["REQUEST_HEADER"] = apimResponseHeaders;
        apiCallHistory["REQUEST_QUERY_PARAMETER"] = null;
        apiCallHistory["REQUEST_BODY"] = originatingRequest.GetRawText();
        apiCallHistory["RESPONSE_HTTP_STATUS_CODE"] = apimStatusCode;
        apiCallHistory["RESPONSE_BODY"] = rawApimResponse;
        apiCallHistory["RESPONSE_TIME_MS"] = apimResponseTimeMs;
        apiCallHistory["ERROR_MESSAGE"] = errorMessage;
        apiCallHistory["ERROR_STACK"] = errorStack;
        payload["API_CALL_HISTORY"] = apiCallHistory;

        if (isErrorResponse)
        {
            payload["INITIAL_REQUEST_RESPONSE"] = null;
            payload["UPDATE_REQUEST_RESPONSE"] = null;
            return payload;
        }

        // DOCMP's response body contains these business fields at its root. This is
        // separate from INITIAL_REQUEST_METADATA, which belongs to the RTA request.
        var docmpResponse = JsonNode.Parse(apimResponse.GetRawText()) as JsonObject ?? new JsonObject();

        var initialResponse = new JsonObject();
        SetResponseValue(initialResponse, "PATIENT_ICN", docmpResponse, "patientIcn");
        SetResponseValue(initialResponse, "SPONSOR_ICN", docmpResponse, "sponsorIcn");
        SetResponseValue(initialResponse, "PROGRAM", docmpResponse, "program");

        var benefitPeriod = new JsonObject();
        SetResponseValue(benefitPeriod, "START_DT", docmpResponse["benefitPeriod"], "startDate");
        SetResponseValue(benefitPeriod, "END_DT", docmpResponse["benefitPeriod"], "endDate");
        initialResponse["BENEFIT_PERIOD"] = benefitPeriod;

        initialResponse["INDIVIDUAL_DEDUCTIBLE"] = MapAccumulator(docmpResponse, "individualDeductible");
        initialResponse["FAMILY_DEDUCTIBLE"] = MapAccumulator(docmpResponse, "familyDeductible");
        initialResponse["OUT_OF_POCKET_MAX"] = MapAccumulator(docmpResponse, "outOfPocketMax");

        payload["INITIAL_REQUEST_RESPONSE"] = initialResponse;
        payload["UPDATE_REQUEST_RESPONSE"] = null;
        return payload;
    }

    private static JsonObject MapAccumulator(JsonObject source, string propertyName)
    {
        // Copy the JSON numbers directly so fractional currency values remain decimals.
        var accumulator = new JsonObject();
        var sourceObject = source[propertyName];
        SetResponseValue(accumulator, "CURRENT", sourceObject, "current");
        SetResponseValue(accumulator, "MAX", sourceObject, "max");
        return accumulator;
    }

    private static void SetResponseValue(JsonObject target, string targetName, JsonNode? sourceObject, string sourceName)
    {
        if (sourceObject is JsonObject jsonObject &&
            jsonObject.TryGetPropertyValue(sourceName, out var value) &&
            value is not null)
        {
            target[targetName] = value.DeepClone();
            return;
        }

        target[targetName] = null;
    }

    private static void SetResponseValue(JsonObject target, string targetName, JsonElement source, string sourceName)
    {
        if (TryGetPropertyIgnoreCase(source, sourceName, out var value) &&
            value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            target[targetName] = JsonNode.Parse(value.GetRawText());
            return;
        }

        target[targetName] = null;
    }

    private static JsonObject BuildLegacyRtaStatusPayload(JsonElement apimResponse, JsonElement originatingRequest)
    {
        var payload = new JsonObject();
        CopyJsonProperty(originatingRequest, payload, "REQUEST_ID");
        CopyJsonProperty(originatingRequest, payload, "CORRELATION_ID");
        CopyJsonProperty(originatingRequest, payload, "IDEMPOTENCY_KEY");
        CopyJsonProperty(originatingRequest, payload, "REQUEST_TYPE");

        SetFromResponse(payload, apimResponse, "REQUEST_STATUS_TYPE", "REQUEST_STATUS_TYPE", "STATUS");
        SetFromResponse(payload, apimResponse, "RESPONSE_CODE", "RESPONSE_CODE");
        SetFromResponse(payload, apimResponse, "PROCESS_DT", "PROCESS_DT", "RESPONSE_DTM");

        var metadata = GetJsonProperty(apimResponse, "METADATA") as JsonObject ?? new JsonObject();
        var requestType = GetScalar(originatingRequest, "REQUEST_TYPE");
        var isUpdate = requestType?.Contains("UPDATE", StringComparison.OrdinalIgnoreCase) == true;
        var mappedResponse = GetJsonProperty(apimResponse, isUpdate ? "UPDATE_RESPONSE" : "INITIAL_RESPONSE_INBD");

        if (mappedResponse is JsonObject responseFields)
        {
            SetMappedValue(metadata, responseFields, "patientIcn", "MBR_ICN");
            if (!isUpdate)
            {
                SetMappedValue(metadata, responseFields, "sponsorIcn", "SPON_ICN");
                SetNestedMappedValue(metadata, "benefitPeriod", "startDate", responseFields, "START_DOS");
                SetNestedMappedValue(metadata, "benefitPeriod", "endDate", responseFields, "END_DOS");
            }

            SetNestedMappedValue(metadata, isUpdate ? "updatedAccumulator.individualDeductible" : "individualDeductible", "current", responseFields, "CUR_IND_DEDUCTIBLE");
            SetNestedMappedValue(metadata, isUpdate ? "updatedAccumulator.individualDeductible" : "individualDeductible", "max", responseFields, "MAX_IND_DEDUCTIBLE");
            SetNestedMappedValue(metadata, isUpdate ? "updatedAccumulator.familyDeductible" : "familyDeductible", "current", responseFields, "CUR_FAM_DEDUCTIBLE");
            SetNestedMappedValue(metadata, isUpdate ? "updatedAccumulator.familyDeductible" : "familyDeductible", "max", responseFields, "MAX_FAM_DEDUCTIBLE");
            SetNestedMappedValue(metadata, isUpdate ? "updatedAccumulator.outOfPocketMax" : "outOfPocketMax", "current", responseFields, "CUR_OUT_OF_POCKET");
            SetNestedMappedValue(metadata, isUpdate ? "updatedAccumulator.outOfPocketMax" : "outOfPocketMax", "max", responseFields, "MAX_OUT_OF_POCKET");
        }

        payload["METADATA"] = metadata;
        return payload;
    }

    private static JsonNode? GetJsonProperty(JsonElement source, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(source, propertyName, out var value))
        {
            return null;
        }

        return JsonNode.Parse(value.GetRawText());
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement source, string propertyName, out JsonElement value)
    {
        if (source.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in source.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static void SetFromResponse(
        JsonObject target,
        JsonElement source,
        string targetName,
        params string[] sourceNames)
    {
        foreach (var sourceName in sourceNames)
        {
            var value = GetJsonProperty(source, sourceName);
            if (value is not null)
            {
                target[targetName] = value;
                return;
            }
        }
    }

    private static void CopyJsonProperty(JsonElement source, JsonObject target, string propertyName)
    {
        var value = GetJsonProperty(source, propertyName);
        if (value is not null)
        {
            target[propertyName] = value;
        }
    }

    private static string? GetScalar(JsonElement source, string propertyName)
    {
        if (source.ValueKind != JsonValueKind.Object ||
            !source.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static void SetMappedValue(JsonObject target, JsonObject source, string targetName, string sourceName)
    {
        if (source.TryGetPropertyValue(sourceName, out var value) && value is not null)
        {
            target[targetName] = value.DeepClone();
        }
    }

    private static void SetNestedMappedValue(
        JsonObject target,
        string targetPath,
        string targetName,
        JsonObject source,
        string sourceName)
    {
        if (!source.TryGetPropertyValue(sourceName, out var value) || value is null)
        {
            return;
        }

        var pathParts = targetPath.Split('.');
        var current = target;
        foreach (var pathPart in pathParts)
        {
            if (current[pathPart] is not JsonObject child)
            {
                child = new JsonObject();
                current[pathPart] = child;
            }

            current = child;
        }

        current[targetName] = value.DeepClone();
    }

    private static bool TryGetResponseCorrelationId(JsonElement item, out string correlationId)
    {
        correlationId = string.Empty;
        if (item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!item.TryGetProperty("CORRELATION_ID", out var value) &&
            !item.TryGetProperty("correlationId", out value))
        {
            return false;
        }

        correlationId = value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.ToString();

        return !string.IsNullOrWhiteSpace(correlationId);
    }

    private sealed record ApimErrorDetails(string? Code, string Message, string? Stack, bool IsDocmpErrorEnvelope);

    private static bool TryParseApimError(string responseJson, int httpStatusCode, out ApimErrorDetails error)
    {
        error = default!;
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(responseJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var docmpErrorCode = TryGetStringProperty(root, "errorCode");
            var code = docmpErrorCode
                ?? TryGetStringProperty(root, "code");
            var message = TryGetStringProperty(root, "message")
                ?? TryGetStringProperty(root, "detail")
                ?? TryGetStringProperty(root, "title");
            var stack = TryGetStringProperty(root, "stack")
                ?? TryGetStringProperty(root, "stackTrace")
                ?? TryGetStringProperty(root, "errorStack");
            var title = TryGetStringProperty(root, "title");
            var detail = TryGetStringProperty(root, "detail");
            var hasErrorsField = TryGetPropertyIgnoreCase(root, "errors", out var errors);
            var hasErrorStatus = TryGetPropertyIgnoreCase(root, "status", out var status) &&
                status.ValueKind == JsonValueKind.Number &&
                status.TryGetInt32(out var embeddedStatusCode) &&
                embeddedStatusCode >= 400;
            var hasErrorContext = TryGetPropertyIgnoreCase(root, "path", out _) ||
                TryGetPropertyIgnoreCase(root, "traceId", out _);
            var hasErrorMarker = code is not null ||
                title is not null ||
                detail is not null ||
                hasErrorsField ||
                hasErrorStatus ||
                (httpStatusCode >= 400 && message is not null) ||
                (hasErrorContext && message is not null);
            if (!hasErrorMarker)
            {
                return false;
            }

            var detailMessages = new List<string>();
            if (hasErrorsField)
            {
                if (errors.ValueKind == JsonValueKind.Array)
                {
                    detailMessages.AddRange(errors.EnumerateArray()
                        .Where(value => value.ValueKind == JsonValueKind.String)
                        .Select(value => value.GetString()!)
                        .Where(value => !string.IsNullOrWhiteSpace(value)));
                }
                else if (errors.ValueKind == JsonValueKind.Object)
                {
                    foreach (var field in errors.EnumerateObject())
                    {
                        if (field.Value.ValueKind == JsonValueKind.Array)
                        {
                            detailMessages.AddRange(field.Value.EnumerateArray()
                                .Where(value => value.ValueKind == JsonValueKind.String)
                                .Select(value => $"{field.Name}: {value.GetString()}")
                                .Where(value => !string.IsNullOrWhiteSpace(value)));
                        }
                        else if (field.Value.ValueKind == JsonValueKind.String &&
                                 !string.IsNullOrWhiteSpace(field.Value.GetString()))
                        {
                            detailMessages.Add($"{field.Name}: {field.Value.GetString()}");
                        }
                    }
                }
                else if (errors.ValueKind == JsonValueKind.String &&
                         !string.IsNullOrWhiteSpace(errors.GetString()))
                {
                    detailMessages.Add(errors.GetString()!);
                }
            }

            var messageParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(code))
            {
                messageParts.Add(code);
            }
            if (!string.IsNullOrWhiteSpace(message))
            {
                messageParts.Add(message);
            }
            if (detailMessages.Count > 0)
            {
                messageParts.AddRange(detailMessages.Distinct(StringComparer.Ordinal));
            }

            if (messageParts.Count == 0)
            {
                messageParts.Add("APIM returned a JSON error response.");
            }

            var combinedMessage = string.Join(" — ", messageParts);
            if (combinedMessage.Length > 2000)
            {
                combinedMessage = combinedMessage[..2000];
            }

            error = new ApimErrorDetails(
                code,
                combinedMessage,
                stack,
                !string.IsNullOrWhiteSpace(docmpErrorCode));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? TryGetStringProperty(JsonElement source, string propertyName)
    {
        return TryGetPropertyIgnoreCase(source, propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
    }

    private string? TryGetBusinessCode(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(_options.JsonResponseCodeField))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Object)
        {
            return TryGetBusinessCodeValue(root);
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var codes = root
            .EnumerateArray()
            .Select(TryGetBusinessCodeValue)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return codes.Length switch
        {
            0 => null,
            1 => codes[0],
            _ => "MIXED"
        };
    }

    private string? TryGetBusinessCodeValue(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty(_options.JsonResponseCodeField, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => value.ToString()
        };
    }

    private static IReadOnlyList<ClaimProcessingResult> BuildResults(
        IEnumerable<string> correlationIds,
        ClaimProcessingDisposition disposition,
        string reason,
        int? httpStatusCode = null,
        string? businessCode = null) =>
        correlationIds
            .Select(id => new ClaimProcessingResult(id, disposition, reason, httpStatusCode, businessCode))
            .ToArray();

    private static void CompleteTelemetry(
        Microsoft.ApplicationInsights.DataContracts.RequestTelemetry request,
        bool success,
        string responseCode,
        long elapsedMilliseconds)
    {
        request.Success = success;
        request.ResponseCode = responseCode;
        request.Duration = TimeSpan.FromMilliseconds(elapsedMilliseconds);
    }
}

