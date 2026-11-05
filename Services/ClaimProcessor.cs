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
    IPollBatchLedger pollBatchLedger,
    PayloadTransformer transformer,
    OutboundCircuitBreaker circuitBreaker,
    ProcessingConcurrencyGates concurrencyGates,
    ApimOutageTracker outageTracker,
    IOptions<ClaimProcessorOptions> options,
    TelemetryClient telemetry,
    ILogger<ClaimProcessor> logger) : IClaimProcessor
{
    private readonly ClaimProcessorOptions _options = options.Value;
    private readonly ProcessingConcurrencyGates _concurrencyGates = concurrencyGates;
    private readonly HashSet<string> _jsonRetryCodes = options.Value.GetJsonRetryCodeSet();
    private readonly HashSet<string> _jsonAlertCodes = options.Value.GetJsonAlertCodeSet();

    public async Task<IReadOnlyList<ClaimProcessingResult>> BufferBatchAsync(
        IReadOnlyList<JsonElement> claims,
        string reason,
        IReadOnlyList<string> rtaResponsePayloads,
        CancellationToken cancellationToken,
        string? pollRunId = null)
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
            await QueueAllAsync(claims, transformed, reason, cancellationToken, pollRunId: pollRunId);
        }
        catch (Exception ex)
        {
            outageTracker.RecordBufferFailure();
            await NotifyServiceBusFailureAsync(claims, reason, ex, cancellationToken);
            throw;
        }

        var bufferedAtUtc = DateTimeOffset.UtcNow;
        await TryRecordHistoryAsync(
            () => requestHistoryStore.RecordServiceBusAsync(
                batchId,
                ids,
                rtaRequestIds,
                rtaResponsePayloads,
                reason,
                bufferedAtUtc,
                cancellationToken),
            "service bus buffer");
        if (!string.IsNullOrWhiteSpace(pollRunId))
        {
            for (var index = 0; index < claims.Count; index++)
            {
                await TryRecordHistoryAsync(
                    () => pollBatchLedger.RecordServiceBusBufferedAsync(
                        pollRunId,
                        index,
                        reason,
                        bufferedAtUtc,
                        cancellationToken),
                    "poll batch Service Bus buffering");
            }
        }

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
        DateTimeOffset? recordsReceivedAtUtc = null,
        long? rtaPullDurationMs = null,
        RetryEnvelope? retryEnvelope = null,
        string? pollRunId = null)
    {
        var mode = _options.GetProcessingConcurrencyMode();
        var maxConcurrency = mode == ProcessingConcurrencyMode.Sequential
            ? 1
            : _options.GetProcessingMaxConcurrency();
        var resultsByClaim = new IReadOnlyList<ClaimProcessingResult>?[claims.Count];

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
                var effectivePollRunId = pollRunId ?? retryEnvelope?.PollRunId;
                var effectivePollRecordOrdinal = retryEnvelope?.PollRecordOrdinal ?? index;
                var result = await ProcessSingleClaimAsync(
                    [claims[index]],
                    rtaResponsePayloads,
                    itemCancellationToken,
                    recordsReceivedAtUtc,
                    rtaPullDurationMs,
                    _concurrencyGates.ApimRequest,
                    _concurrencyGates.RtaWriteback,
                    retryEnvelope,
                    effectivePollRunId,
                    effectivePollRecordOrdinal);
                resultsByClaim[index] = result;
                if (!string.IsNullOrWhiteSpace(effectivePollRunId))
                {
                    foreach (var item in result)
                    {
                        if (item.Disposition is ClaimProcessingDisposition.QueuedForRetry or ClaimProcessingDisposition.QueuedForWritebackRetry)
                        {
                            await TryRecordHistoryAsync(
                                () => pollBatchLedger.RecordServiceBusBufferedAsync(
                                    effectivePollRunId,
                                    effectivePollRecordOrdinal,
                                    item.Reason,
                                    DateTimeOffset.UtcNow,
                                    itemCancellationToken),
                                "poll batch retry buffering");
                        }
                    }
                }
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
        DateTimeOffset? recordsReceivedAtUtc,
        long? rtaPullDurationMs,
        SemaphoreSlim apimRequestGate,
        SemaphoreSlim rtaWritebackGate,
        RetryEnvelope? retryEnvelope,
        string? pollRunId,
        int pollRecordOrdinal)
    {
        if (claims.Count == 0)
        {
            return [];
        }

        var pipelineStartedAtUtc = recordsReceivedAtUtc ?? DateTimeOffset.UtcNow;
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

        if (retryEnvelope is not null &&
            DateTimeOffset.UtcNow >= (retryEnvelope.RetryUntilUtc
                ?? retryEnvelope.QueuedAtUtc.AddMinutes(_options.ServiceBusMessageTtlMinutes)))
        {
            var finalResult = await FinalizeApimRetryAsync(
                claims[0],
                retryEnvelope,
                batchId,
                correlationIds[0],
                cancellationToken,
                rtaWritebackGate);
            CompleteTelemetry(operation.Telemetry, false, finalResult.Single().Reason, stopwatch.ElapsedMilliseconds);
            return finalResult;
        }

        if (!circuitBreaker.CanAttempt())
        {
            try
            {
                await QueueAllAsync(claims, transformedClaims, "OutboundCircuitOpen", cancellationToken, retryContext: retryEnvelope, pollRunId: pollRunId, pollRecordOrdinal: pollRecordOrdinal);
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

        var apimAttemptedAtUtc = DateTimeOffset.UtcNow;
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
                apimAttemptedAtUtc,
                cancellationToken),
            "APIM request");
        if (!string.IsNullOrWhiteSpace(pollRunId))
        {
            await TryRecordHistoryAsync(
                () => pollBatchLedger.RecordApimAttemptAsync(
                    pollRunId,
                    pollRecordOrdinal,
                    apimAttemptedAtUtc,
                    cancellationToken),
                "poll batch APIM attempt");
        }

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
            if (_options.ApimAuthenticationEnabled)
            {
                try
                {
                    await authHeaderProvider.ApplyBearerTokenAsync(request, _options.ApimScope, cancellationToken);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    await TryRecordHistoryAsync(
                        () => requestHistoryStore.RecordApimAuthStatusAsync(
                            batchId,
                            correlationIds,
                            false,
                            null,
                            DateTimeOffset.UtcNow,
                            CancellationToken.None),
                        "APIM bearer-token acquisition failure");
                    throw;
                }
            }

            var bearerTokenAttached = _options.ApimAuthenticationEnabled &&
                string.Equals(request.Headers.Authorization?.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(request.Headers.Authorization?.Parameter);
            await TryRecordHistoryAsync(
                () => requestHistoryStore.RecordApimAuthStatusAsync(
                    batchId,
                    correlationIds,
                    bearerTokenAttached,
                    null,
                    DateTimeOffset.UtcNow,
                    cancellationToken),
                "APIM bearer-token attachment");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.ApimRequestTimeoutSeconds));

            apimRoundTripStopwatch.Start();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token);
            var responseJson = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            apimRoundTripStopwatch.Stop();
            apimRequestGate.Release();
            apimRequestGateHeld = false;
            var apimResponseReceivedAtUtc = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(pollRunId))
            {
                await TryRecordHistoryAsync(
                    () => pollBatchLedger.RecordApimResponseAsync(
                        pollRunId,
                        pollRecordOrdinal,
                        (int)response.StatusCode,
                        apimResponseReceivedAtUtc,
                        cancellationToken),
                    "poll batch APIM response");
            }
            var authConfirmed = response.Headers.TryGetValues("X-Pulse-Auth-Validated", out var authConfirmationValues)
                && authConfirmationValues.Any(value => string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
            await TryRecordHistoryAsync(
                () => requestHistoryStore.RecordApimAuthStatusAsync(
                    batchId,
                    correlationIds,
                    null,
                    authConfirmed,
                    apimResponseReceivedAtUtc,
                    cancellationToken),
                "APIM bearer-token validation confirmation");

            var statusCode = (int)response.StatusCode;
            var hasApimError = TryParseApimError(responseJson, statusCode, out var apimError);
            var isDocmpErrorEnvelope = hasApimError && apimError.IsDocmpErrorEnvelope;

            // DOCMP JSON error envelopes are application failures and are written back to RTA.
            // Transport/non-business HTTP failures use one bounded Service Bus retry window.
            if (statusCode != 200 && !isDocmpErrorEnvelope)
            {
                circuitBreaker.RecordFailure();
                outageTracker.RecordApimFailure();
                var reason = $"Http{statusCode}";
                var responseHeaders = SerializeApimResponseHeaders(response);
                var retryUntilUtc = retryEnvelope?.RetryUntilUtc
                    ?? (retryEnvelope?.QueuedAtUtc ?? DateTimeOffset.UtcNow)
                        .AddMinutes(_options.ServiceBusMessageTtlMinutes);

                if (retryEnvelope is not null && DateTimeOffset.UtcNow >= retryUntilUtc)
                {
                    var exhaustedEnvelope = retryEnvelope with
                    {
                        RetryUntilUtc = retryUntilUtc,
                        LastHttpStatusCode = statusCode,
                        LastHttpReasonPhrase = response.ReasonPhrase,
                        LastHttpResponseBody = responseJson,
                        LastHttpResponseHeaders = responseHeaders,
                        LastResponseTimeMs = apimRoundTripStopwatch.ElapsedMilliseconds
                    };
                    var finalResult = await FinalizeApimRetryAsync(
                        claims[0],
                        exhaustedEnvelope,
                        batchId,
                        correlationIds[0],
                        cancellationToken,
                        rtaWritebackGate);
                    CompleteTelemetry(operation.Telemetry, false, finalResult.Single().Reason, stopwatch.ElapsedMilliseconds);
                    return finalResult;
                }

                try
                {
                    await QueueAllAsync(
                        claims,
                        transformedClaims,
                        reason,
                        cancellationToken,
                        retryEnvelope,
                        statusCode,
                        response.ReasonPhrase,
                        responseJson,
                        responseHeaders,
                        apimRoundTripStopwatch.ElapsedMilliseconds,
                        pollRunId: pollRunId,
                        pollRecordOrdinal: pollRecordOrdinal);
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
                    await NotifyServiceBusFailureAsync(
                        claims,
                        reason,
                        ex,
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

                CompleteTelemetry(operation.Telemetry, false, $"HTTP {statusCode}", stopwatch.ElapsedMilliseconds);
                return BuildResults(correlationIds, ClaimProcessingDisposition.QueuedForRetry, reason, statusCode);
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
                        apimError.Stack,
                        outboundApimRequestBody: outboundPayloadJson,
                        pollRunId: pollRunId,
                        pollRecordOrdinal: pollRecordOrdinal);
                    writebackStopwatch.Stop();
                    var writebackCompletedAtUtc = DateTimeOffset.UtcNow;
                    if (!string.IsNullOrWhiteSpace(pollRunId))
                    {
                        await TryRecordHistoryAsync(
                            () => pollBatchLedger.RecordRtaWritebackAsync(
                                pollRunId,
                                pollRecordOrdinal,
                                writebackResult.Reason == "RtaWritebackQueued" ? null : writebackResult.Success,
                                writebackResult.Details?.HttpStatusCode,
                                writebackResult.Reason,
                                writebackCompletedAtUtc,
                                cancellationToken),
                            "poll batch RTA writeback result");
                    }

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
                    await QueueAllAsync(claims, transformedClaims, $"JsonCode:{businessCode}", cancellationToken, retryContext: retryEnvelope, pollRunId: pollRunId, pollRecordOrdinal: pollRecordOrdinal);
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
                    rtaWritebackGate,
                    outboundApimRequestBody: outboundPayloadJson,
                    pollRunId: pollRunId,
                    pollRecordOrdinal: pollRecordOrdinal);
                writebackStopwatch.Stop();
                var writebackCompletedAtUtc = DateTimeOffset.UtcNow;
                if (!string.IsNullOrWhiteSpace(pollRunId))
                {
                    await TryRecordHistoryAsync(
                        () => pollBatchLedger.RecordRtaWritebackAsync(
                            pollRunId,
                            pollRecordOrdinal,
                            writebackResult.Reason == "RtaWritebackQueued" ? null : writebackResult.Success,
                            writebackResult.Details?.HttpStatusCode,
                            writebackResult.Reason,
                            writebackCompletedAtUtc,
                            cancellationToken),
                        "poll batch RTA writeback result");
                }
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
                await QueueAllAsync(claims, transformedClaims, "ApimTimeout", cancellationToken, retryContext: retryEnvelope, pollRunId: pollRunId, pollRecordOrdinal: pollRecordOrdinal);
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
                await QueueAllAsync(claims, transformedClaims, "NetworkFailure", cancellationToken, retryContext: retryEnvelope, pollRunId: pollRunId, pollRecordOrdinal: pollRecordOrdinal);
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
        CancellationToken cancellationToken,
        RetryEnvelope? retryContext = null,
        int? lastHttpStatusCode = null,
        string? lastHttpReasonPhrase = null,
        string? lastHttpResponseBody = null,
        string? lastHttpResponseHeaders = null,
        long? lastResponseTimeMs = null,
        string? pollRunId = null,
        int pollRecordOrdinal = 0)
    {
        for (var i = 0; i < claims.Count; i++)
        {
            var claim = claims[i];
            var uniqueId = GetCorrelationId(claim);
            TryGetScalar(claim, "REQUEST_ID", out var requestId);
            TryGetScalar(claim, "IDEMPOTENCY_KEY", out var idempotencyKey);
            var queuedAtUtc = retryContext?.QueuedAtUtc ?? DateTimeOffset.UtcNow;

            var envelope = new RetryEnvelope(
                uniqueId,
                string.IsNullOrWhiteSpace(requestId) ? null : requestId,
                string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey,
                reason,
                queuedAtUtc,
                claim.GetRawText(),
                transformedClaims[i].ToJsonString())
            {
                PollRunId = retryContext?.PollRunId ?? pollRunId,
                PollRecordOrdinal = retryContext?.PollRecordOrdinal ?? pollRecordOrdinal + i,
                RetryUntilUtc = retryContext?.RetryUntilUtc
                    ?? queuedAtUtc.AddMinutes(_options.ServiceBusMessageTtlMinutes),
                RetryAttempt = (retryContext?.RetryAttempt ?? 0) + (retryContext is null ? 0 : 1),
                LastHttpStatusCode = lastHttpStatusCode ?? retryContext?.LastHttpStatusCode,
                LastHttpReasonPhrase = lastHttpReasonPhrase ?? retryContext?.LastHttpReasonPhrase,
                LastHttpResponseBody = lastHttpResponseBody ?? retryContext?.LastHttpResponseBody,
                LastHttpResponseHeaders = lastHttpResponseHeaders ?? retryContext?.LastHttpResponseHeaders,
                LastResponseTimeMs = lastResponseTimeMs ?? retryContext?.LastResponseTimeMs
            };

            await retryQueue.EnqueueAsync(envelope, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<ClaimProcessingResult>> FinalizeApimRetryAsync(
        JsonElement claim,
        RetryEnvelope envelope,
        string batchId,
        string correlationId,
        CancellationToken cancellationToken,
        SemaphoreSlim rtaWritebackGate)
    {
        var statusCode = envelope.LastHttpStatusCode ?? 503;
        var responseBody = envelope.LastHttpResponseBody ?? string.Empty;
        var responseHeaders = envelope.LastHttpResponseHeaders ?? string.Empty;
        var responseTimeMs = envelope.LastResponseTimeMs ?? 0;
        var reasonPhrase = envelope.LastHttpReasonPhrase ?? "No successful APIM response";
        var errorMessage = $"We were unable to contact DOCMP for this record after retrying for {_options.ServiceBusMessageTtlMinutes} minutes. Last APIM HTTP status: {statusCode} {reasonPhrase}.";
        if (!string.IsNullOrWhiteSpace(responseBody))
        {
            errorMessage += " Last response: " + responseBody;
        }

        logger.LogError(
            "APIM retry window exhausted for claim {UniqueId}. LastHttpStatus={StatusCode} Attempts={RetryAttempt}. Sending final failure status to RTA.",
            envelope.UniqueId,
            statusCode,
            envelope.RetryAttempt);

        var succeeded = !_options.RtaStatusWritebackEnabled;
        var finalReason = "ApimRetryWindowExhausted";
        if (_options.RtaStatusWritebackEnabled)
        {
            var syntheticResponse = JsonSerializer.Serialize(new[]
            {
                new { CORRELATION_ID = correlationId }
            });
            var writeback = await WriteBatchResultsAsync(
                syntheticResponse,
                statusCode,
                DateTimeOffset.UtcNow,
                responseTimeMs,
                _options.ApimBaseUrl,
                responseHeaders,
                batchId,
                [claim],
                [correlationId],
                cancellationToken,
                rtaWritebackGate,
                errorMessage,
                null,
                rawApimResponseBody: responseBody,
                outboundApimRequestBody: envelope.TransformedPayload,
                pollRunId: envelope.PollRunId,
                pollRecordOrdinal: envelope.PollRecordOrdinal ?? 0);
            if (!string.IsNullOrWhiteSpace(envelope.PollRunId) && envelope.PollRecordOrdinal is { } pollRecordOrdinal)
            {
                await TryRecordHistoryAsync(
                    () => pollBatchLedger.RecordRtaWritebackAsync(
                        envelope.PollRunId,
                        pollRecordOrdinal,
                        writeback.Reason == "RtaWritebackQueued" ? null : writeback.Success,
                        writeback.Details?.HttpStatusCode,
                        writeback.Reason,
                        DateTimeOffset.UtcNow,
                        cancellationToken),
                    "poll batch exhausted-retry RTA writeback result");
            }

            succeeded = writeback.Success;
            finalReason = writeback.Reason == "RtaWritebackQueued"
                ? "ApimRetryExhaustedWritebackQueued"
                : writeback.Reason == "WritebackCompleted"
                    ? finalReason
                    : "ApimRetryExhaustedWritebackFailed";
        }

        await TryRecordHistoryAsync(
            () => requestHistoryStore.RecordApimResultAsync(
                batchId,
                [correlationId],
                succeeded ? "Alerted" : "WritebackFailed",
                statusCode,
                null,
                responseBody,
                finalReason,
                false,
                DateTimeOffset.UtcNow,
                cancellationToken,
                DateTimeOffset.UtcNow,
                responseTimeMs),
            "exhausted APIM retry window");

        telemetry.TrackEvent("ApimRetryWindowExhausted", new Dictionary<string, string>
        {
            ["uniqueId"] = envelope.UniqueId,
            ["lastHttpStatus"] = statusCode.ToString(),
            ["retryAttempt"] = envelope.RetryAttempt.ToString()
        });

        return BuildResults(
            [correlationId],
            succeeded || finalReason == "ApimRetryExhaustedWritebackQueued"
                ? ClaimProcessingDisposition.Alerted
                : ClaimProcessingDisposition.FailedWriteback,
            finalReason,
            statusCode);
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
        string? apimErrorStack = null,
        string? rawApimResponseBody = null,
        string? outboundApimRequestBody = null,
        string? pollRunId = null,
        int pollRecordOrdinal = 0)
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
                    rawApimResponseBody ?? responseJson,
                    apimStatusCode,
                    apimResponseReceivedAtUtc,
                    apimResponseTimeMs,
                    _options.ApimBaseUrl,
                    _options.ApimAdjudicationPath,
                    apimResponseHeaders,
                    outboundApimRequestBody ?? string.Empty,
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
                        apimResponseHeaders,
                        outboundApimRequestBody ?? string.Empty)
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
                    statusPayloadJson)
                {
                    PollRunId = pollRunId,
                    PollRecordOrdinal = pollRecordOrdinal
                };

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
        string outboundApimRequestBody,
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
        apiCallHistory["REQUEST_BODY"] = outboundApimRequestBody;
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

