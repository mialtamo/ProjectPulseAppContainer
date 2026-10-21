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
    IRequestHistoryStore requestHistoryStore,
    PayloadTransformer transformer,
    OutboundCircuitBreaker circuitBreaker,
    ApimOutageTracker outageTracker,
    IOptions<ClaimProcessorOptions> options,
    TelemetryClient telemetry,
    ILogger<ClaimProcessor> logger) : IClaimProcessor
{
    private readonly ClaimProcessorOptions _options = options.Value;
    private readonly HashSet<int> _retryHttpCodes = options.Value.GetRetryHttpStatusCodeSet();
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
        var batchId = Guid.NewGuid().ToString("N");

        try
        {
            await QueueAllAsync(claims, transformed, reason, cancellationToken);
        }
        catch
        {
            outageTracker.RecordBufferFailure();
            throw;
        }

        await TryRecordHistoryAsync(
            () => requestHistoryStore.RecordServiceBusAsync(
                batchId,
                ids,
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
        CancellationToken cancellationToken)
    {
        var results = new List<ClaimProcessingResult>(claims.Count);
        foreach (var claim in claims)
        {
            var result = await ProcessSingleClaimAsync([claim], rtaResponsePayloads, cancellationToken);
            results.AddRange(result);
        }

        return results;
    }

    private async Task<IReadOnlyList<ClaimProcessingResult>> ProcessSingleClaimAsync(
        IReadOnlyList<JsonElement> claims,
        IReadOnlyList<string> rtaResponsePayloads,
        CancellationToken cancellationToken)
    {
        if (claims.Count == 0)
        {
            return [];
        }

        var correlationIds = claims.Select(GetCorrelationId).ToArray();
        var transformedClaims = TransformClaims(claims);
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
            catch
            {
                outageTracker.RecordBufferFailure();
                throw;
            }

            await TryRecordHistoryAsync(
                () => requestHistoryStore.RecordServiceBusAsync(
                    batchId,
                    correlationIds,
                    rtaResponsePayloads,
                    "OutboundCircuitOpen",
                    DateTimeOffset.UtcNow,
                    cancellationToken),
                "circuit-open service bus buffer");

            CompleteTelemetry(operation.Telemetry, false, "CircuitOpen", stopwatch.ElapsedMilliseconds);
            return BuildResults(correlationIds, ClaimProcessingDisposition.QueuedForRetry, "OutboundCircuitOpen");
        }

        var transformedArray = new JsonArray();
        foreach (var transformed in transformedClaims)
        {
            transformedArray.Add(transformed.DeepClone());
        }

        JsonNode outboundPayload = string.IsNullOrWhiteSpace(_options.ApimBatchRootProperty)
            ? transformedArray
            : new JsonObject { [_options.ApimBatchRootProperty] = transformedArray };

        var outboundPayloadJson = outboundPayload.ToJsonString();

        await TryRecordHistoryAsync(
            () => requestHistoryStore.RecordApimAttemptAsync(
                batchId,
                correlationIds,
                rtaResponsePayloads,
                outboundPayloadJson,
                DateTimeOffset.UtcNow,
                cancellationToken),
            "APIM request");

        if (_options.ShouldLogPollPayloads())
        {
            logger.LogInformation(
                "APIM outbound batch {BatchId} payload: {Payload}",
                batchId,
                outboundPayloadJson);
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
        request.Headers.TryAddWithoutValidation("X-Pulse-Record-Count", "1");
        await authHeaderProvider.ApplyBearerTokenAsync(request, _options.ApimScope, cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.ApimRequestTimeoutSeconds));

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token);
            var responseJson = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            var statusCode = (int)response.StatusCode;

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
                        cancellationToken),
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

            if (_options.ShouldLogPollPayloads())
            {
                logger.LogInformation(
                    "APIM batch {BatchId} response. Status={StatusCode} Payload={Payload}",
                    batchId,
                    statusCode,
                    responseJson);
            }

            if (!response.IsSuccessStatusCode)
            {
                circuitBreaker.RecordFailure();
                outageTracker.RecordApimFailure();

                if (_retryHttpCodes.Contains(statusCode))
                {
                    try
                    {
                        await QueueAllAsync(claims, transformedClaims, $"Http{statusCode}", cancellationToken);
                    }
                    catch
                    {
                        outageTracker.RecordBufferFailure();
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
                            $"Http{statusCode}",
                            true,
                            DateTimeOffset.UtcNow,
                            cancellationToken),
                        "retryable HTTP response");

                    CompleteTelemetry(operation.Telemetry, false, $"HTTP {statusCode}", stopwatch.ElapsedMilliseconds);
                    return BuildResults(correlationIds, ClaimProcessingDisposition.QueuedForRetry, $"Http{statusCode}", statusCode);
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
                        $"Http{statusCode}",
                        false,
                        DateTimeOffset.UtcNow,
                        cancellationToken),
                    "non-retry HTTP response");

                CompleteTelemetry(operation.Telemetry, false, $"HTTP {statusCode}", stopwatch.ElapsedMilliseconds);
                return BuildResults(correlationIds, ClaimProcessingDisposition.Alerted, $"Http{statusCode}", statusCode);
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
                catch
                {
                    outageTracker.RecordBufferFailure();
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
                        cancellationToken),
                    "JSON retry response");

                CompleteTelemetry(operation.Telemetry, false, $"JSON {businessCode}", stopwatch.ElapsedMilliseconds);
                return BuildResults(correlationIds, ClaimProcessingDisposition.QueuedForRetry, "JsonRetryCode", statusCode, businessCode);
            }

            if (_options.RtaStatusWritebackEnabled)
            {
                var writebackResult = await WriteBatchResultsAsync(responseJson, claims, correlationIds, cancellationToken);
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
                            cancellationToken),
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
                        cancellationToken),
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
                    cancellationToken),
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
            catch
            {
                outageTracker.RecordBufferFailure();
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
                    cancellationToken),
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
            catch
            {
                outageTracker.RecordBufferFailure();
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
                    cancellationToken),
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

    private async Task<(bool Success, string Reason)> WriteBatchResultsAsync(
        string responseJson,
        IReadOnlyList<JsonElement> claims,
        IReadOnlyList<string> correlationIds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            logger.LogWarning("RTA writeback is enabled but APIM returned an empty response body.");
            return (false, "EmptyApimResponse");
        }

        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        JsonElement[] responseItems = root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray().ToArray(),
            JsonValueKind.Object when correlationIds.Count == 1 => new[] { root },
            _ => Array.Empty<JsonElement>()
        };

        if (responseItems.Length == 0)
        {
            logger.LogWarning(
                "RTA writeback is enabled but APIM response was not a supported object or array.");
            return (false, "InvalidApimResponseShape");
        }

        if (responseItems.Length != correlationIds.Count || claims.Count != correlationIds.Count)
        {
            logger.LogWarning(
                "APIM response item count {ResponseCount}, request count {RequestCount}, and submitted count {SubmittedCount} did not match",
                responseItems.Length,
                claims.Count,
                correlationIds.Count);
            return (false, "ApimResponseCountMismatch");
        }

        var requestsByCorrelationId = claims.ToDictionary(
            GetCorrelationId,
            claim => claim,
            StringComparer.OrdinalIgnoreCase);
        var expectedCorrelationIds = correlationIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var responseCorrelationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in responseItems)
        {
            if (!TryGetResponseCorrelationId(item, out var correlationId))
            {
                logger.LogWarning("APIM response item did not contain a correlation ID. Writeback rejected.");
                return (false, "MissingCorrelationId");
            }

            if (!expectedCorrelationIds.Contains(correlationId))
            {
                logger.LogError(
                    "APIM response contained unexpected correlation ID {CorrelationId}. Writeback rejected.",
                    correlationId);
                return (false, "UnexpectedCorrelationId");
            }

            if (!responseCorrelationIds.Add(correlationId))
            {
                logger.LogError(
                    "APIM response contained duplicate correlation ID {CorrelationId}. Writeback rejected.",
                    correlationId);
                return (false, "DuplicateCorrelationId");
            }
        }

        if (!expectedCorrelationIds.SetEquals(responseCorrelationIds))
        {
            logger.LogError("APIM response correlation IDs did not exactly match the submitted batch. Writeback rejected.");
            return (false, "CorrelationIdSetMismatch");
        }

        foreach (var item in responseItems)
        {
            TryGetResponseCorrelationId(item, out var correlationId);
            var statusPayload = BuildRtaStatusPayload(item, requestsByCorrelationId[correlationId]);
            var written = await appApiClient.WriteStatusAsync(
                correlationId,
                statusPayload.ToJsonString(),
                cancellationToken);

            if (!written)
            {
                logger.LogError(
                    "RTA status writeback failed for correlation ID {CorrelationId}.",
                    correlationId);
                return (false, "RtaStatusWritebackFailed");
            }
        }

        return (true, "WritebackCompleted");
    }

    private static JsonObject BuildRtaStatusPayload(JsonElement apimResponse, JsonElement originatingRequest)
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

    private static JsonNode? GetJsonProperty(JsonElement source, string propertyName)
    {
        if (source.ValueKind != JsonValueKind.Object ||
            !source.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return JsonNode.Parse(value.GetRawText());
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

