using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using System.Text.Json;
using ProjectPulse.Processor.Models;
using ProjectPulse.Processor.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProjectPulse.Processor.Services;

public sealed class AzureStorageRequestHistoryStore : IRequestHistoryStore
{
    private const string PartitionKeyValue = "requests";
    private const string RecentPartitionKeyValue = "!recent";
    private const string IndexMetadataPartitionKey = "!index";
    private const string IndexVersionRowKey = "recent-v1";

    private readonly TableClient _table;
    private readonly BlobContainerClient _container;
    private readonly ClaimProcessorOptions _options;
    private readonly ILogger<AzureStorageRequestHistoryStore> _logger;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private readonly SemaphoreSlim _recentIndexInitializeLock = new(1, 1);
    private readonly SemaphoreSlim _timingAveragesLock = new(1, 1);
    private RequestHistoryTimingAverages? _cachedTimingAverages;
    private string? _cachedTimingAveragesRangeKey;
    private bool _initialized;
    private bool _recentIndexInitialized;

    public AzureStorageRequestHistoryStore(
        IOptions<ClaimProcessorOptions> options,
        ILogger<AzureStorageRequestHistoryStore> logger)
    {
        _options = options.Value;
        _logger = logger;

        var connectionString =
            Environment.GetEnvironmentVariable("RequestHistoryStorageConnectionString")
            ?? Environment.GetEnvironmentVariable("AzureWebJobsStorage")
            ?? throw new InvalidOperationException(
                "Request history requires RequestHistoryStorageConnectionString or AzureWebJobsStorage.");

        _table = new TableClient(connectionString, _options.RequestHistoryTableName);
        _container = new BlobContainerClient(connectionString, _options.RequestHistoryBlobContainerName);
    }

    public async Task RecordApimAttemptAsync(
        string batchId,
        IReadOnlyList<string> uniqueIds,
        IReadOnlyList<string> rtaResponsePayloads,
        string requestPayload,
        DateTimeOffset? pipelineStartedAtUtc,
        long? rtaPullDurationMs,
        long translationDurationMs,
        DateTimeOffset sentAtUtc,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var requestBlob = await SavePayloadAsync(
            batchId,
            "request.json",
            requestPayload,
            cancellationToken);
        var rtaResponseBlobs = await SaveRtaResponsePayloadsAsync(
            batchId,
            rtaResponsePayloads,
            cancellationToken);

        for (var i = 0; i < uniqueIds.Count; i++)
        {
            var entity = new RequestHistoryEntity
            {
                PartitionKey = PartitionKeyValue,
                RowKey = BuildRowKey(batchId, i),
                BatchId = batchId,
                UniqueId = uniqueIds[i],
                CreatedAtUtc = sentAtUtc,
                UpdatedAtUtc = sentAtUtc,
                Status = "SentToApim",
                Destination = "APIM",
                SentToApimAtUtc = sentAtUtc,
                PipelineStartedAtUtc = pipelineStartedAtUtc,
                RtaPullDurationMs = rtaPullDurationMs,
                TranslationDurationMs = translationDurationMs,
                RequestPayloadBlob = requestBlob,
                RtaResponsePayloadBlobs = rtaResponseBlobs,
                PayloadLoggingEnabled = _options.ShouldCaptureRequestHistoryPayloads()
            };

            string? previousIndexRowKey = null;
            try
            {
                var existing = (await _table.GetEntityAsync<RequestHistoryEntity>(
                    PartitionKeyValue,
                    entity.RowKey,
                    cancellationToken: cancellationToken)).Value;
                previousIndexRowKey = BuildRecentIndexRowKey(existing.UpdatedAtUtc, existing.RowKey);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
            }

            await _table.UpsertEntityAsync(entity, TableUpdateMode.Merge, cancellationToken);
            await UpsertRecentIndexAsync(entity, previousIndexRowKey, cancellationToken);
        }
    }

    public async Task RecordApimResultAsync(
        string batchId,
        IReadOnlyList<string> uniqueIds,
        string status,
        int? httpStatusCode,
        string? businessResponseCode,
        string? responsePayload,
        string? reason,
        bool sentToServiceBus,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken,
        DateTimeOffset? apimResponseAtUtc = null,
        long? apimRoundTripDurationMs = null)
    {
        await EnsureInitializedAsync(cancellationToken);

        var responseBlob = string.IsNullOrWhiteSpace(responsePayload)
            ? null
            : await SavePayloadAsync(
                batchId,
                "response.json",
                responsePayload,
                cancellationToken);

        for (var i = 0; i < uniqueIds.Count; i++)
        {
            var rowKey = BuildRowKey(batchId, i);
            RequestHistoryEntity entity;
            string? previousIndexRowKey;

            try
            {
                entity = (await _table.GetEntityAsync<RequestHistoryEntity>(
                    PartitionKeyValue,
                    rowKey,
                    cancellationToken: cancellationToken)).Value;
                previousIndexRowKey = BuildRecentIndexRowKey(entity.UpdatedAtUtc, entity.RowKey);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                previousIndexRowKey = null;
                entity = new RequestHistoryEntity
                {
                    PartitionKey = PartitionKeyValue,
                    RowKey = rowKey,
                    BatchId = batchId,
                    UniqueId = uniqueIds[i],
                    CreatedAtUtc = completedAtUtc
                };
            }
            entity.UpdatedAtUtc = completedAtUtc;
            entity.Status = status;
            entity.Destination = sentToServiceBus ? "ServiceBus" : "APIM";
            entity.HttpStatusCode = httpStatusCode;
            entity.BusinessResponseCode = businessResponseCode;
            entity.Reason = reason;
            var responseAtUtc = apimResponseAtUtc ?? completedAtUtc;
            entity.ApimResponseAtUtc = apimResponseAtUtc;
            if (apimRoundTripDurationMs is { } roundTripDurationMs)
            {
                entity.ApimRoundTripDurationMs = Math.Max(0, roundTripDurationMs);
                entity.SentToApimAtUtc = responseAtUtc.AddMilliseconds(-entity.ApimRoundTripDurationMs.Value);
            }
            else if (apimResponseAtUtc is not null && entity.SentToApimAtUtc is { } sentToApimAtUtc)
            {
                entity.ApimRoundTripDurationMs = Math.Max(0, (long)(responseAtUtc - sentToApimAtUtc).TotalMilliseconds);
            }

            if (entity.PipelineStartedAtUtc is { } pipelineStartedAtUtc)
            {
                entity.EndToEndDurationMs = Math.Max(0, (long)(completedAtUtc - pipelineStartedAtUtc).TotalMilliseconds);
            }

            entity.ResponsePayloadBlob = responseBlob;
            entity.PayloadLoggingEnabled = _options.ShouldCaptureRequestHistoryPayloads();

            if (sentToServiceBus)
            {
                entity.BufferedToServiceBusAtUtc = completedAtUtc;
            }

            await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken);
            await UpsertRecentIndexAsync(entity, previousIndexRowKey, cancellationToken);
        }
    }

    public async Task RecordWritebackTimingAsync(
        string batchId,
        IReadOnlyList<string> uniqueIds,
        long writebackDurationMs,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        for (var i = 0; i < uniqueIds.Count; i++)
        {
            var rowKey = BuildRowKey(batchId, i);
            RequestHistoryEntity entity;

            try
            {
                entity = (await _table.GetEntityAsync<RequestHistoryEntity>(
                    PartitionKeyValue,
                    rowKey,
                    cancellationToken: cancellationToken)).Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                continue;
            }

            entity.RtaWritebackDurationMs = Math.Max(0, writebackDurationMs);
            if (entity.PipelineStartedAtUtc is { } pipelineStartedAtUtc)
            {
                entity.EndToEndDurationMs = Math.Max(0, (long)(completedAtUtc - pipelineStartedAtUtc).TotalMilliseconds);
            }

            var previousIndexRowKey = BuildRecentIndexRowKey(entity.UpdatedAtUtc, entity.RowKey);
            await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken);
            await UpsertRecentIndexAsync(entity, previousIndexRowKey, cancellationToken);
        }
    }

    public async Task RecordRtaStatusPayloadAsync(
        string batchId,
        IReadOnlyList<string> uniqueIds,
        IReadOnlyList<string> payloads,
        CancellationToken cancellationToken)
    {
        if (uniqueIds.Count != payloads.Count)
        {
            throw new ArgumentException("The RTA status payload count must match the request count.", nameof(payloads));
        }

        await EnsureInitializedAsync(cancellationToken);
        for (var i = 0; i < uniqueIds.Count; i++)
        {
            var blobName = await SavePayloadAsync(
                batchId,
                $"rta-status-{i + 1:D3}.json",
                payloads[i],
                cancellationToken);
            if (blobName is null)
            {
                continue;
            }

            var rowKey = BuildRowKey(batchId, i);
            RequestHistoryEntity entity;
            try
            {
                entity = (await _table.GetEntityAsync<RequestHistoryEntity>(
                    PartitionKeyValue,
                    rowKey,
                    cancellationToken: cancellationToken)).Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                entity = new RequestHistoryEntity
                {
                    PartitionKey = PartitionKeyValue,
                    RowKey = rowKey,
                    BatchId = batchId,
                    UniqueId = uniqueIds[i],
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
            }

            entity.RtaStatusPayloadBlob = blobName;
            entity.PayloadLoggingEnabled = _options.ShouldCaptureRequestHistoryPayloads();
            await _table.UpsertEntityAsync(entity, TableUpdateMode.Merge, cancellationToken);
        }
    }

    public async Task RecordServiceBusAsync(
        string batchId,
        IReadOnlyList<string> uniqueIds,
        IReadOnlyList<string> rtaResponsePayloads,
        string reason,
        DateTimeOffset bufferedAtUtc,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var rtaResponseBlobs = await SaveRtaResponsePayloadsAsync(
            batchId,
            rtaResponsePayloads,
            cancellationToken);

        for (var i = 0; i < uniqueIds.Count; i++)
        {
            var rowKey = BuildRowKey(batchId, i);
            RequestHistoryEntity entity;
            string? previousIndexRowKey;

            try
            {
                entity = (await _table.GetEntityAsync<RequestHistoryEntity>(
                    PartitionKeyValue,
                    rowKey,
                    cancellationToken: cancellationToken)).Value;
                previousIndexRowKey = BuildRecentIndexRowKey(entity.UpdatedAtUtc, entity.RowKey);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                previousIndexRowKey = null;
                entity = new RequestHistoryEntity
                {
                    PartitionKey = PartitionKeyValue,
                    RowKey = rowKey,
                    BatchId = batchId,
                    UniqueId = uniqueIds[i],
                    CreatedAtUtc = bufferedAtUtc
                };
            }

            entity.UpdatedAtUtc = bufferedAtUtc;
            entity.Status = "Buffered";
            entity.Destination = "ServiceBus";
            entity.Reason = reason;
            entity.BufferedToServiceBusAtUtc = bufferedAtUtc;
            entity.RtaResponsePayloadBlobs = rtaResponseBlobs;
            entity.PayloadLoggingEnabled = _options.ShouldCaptureRequestHistoryPayloads();

            await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken);
            await UpsertRecentIndexAsync(entity, previousIndexRowKey, cancellationToken);
        }
    }

    public async Task<RequestHistoryPage> ListAsync(
        int limit,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await EnsureRecentIndexReadyAsync(cancellationToken);

        var pageSize = Math.Clamp(limit, 1, 1000);
        var lastRowKey = DecodeContinuationToken(continuationToken);
        var filter = $"PartitionKey eq '{RecentPartitionKeyValue}'";
        if (lastRowKey is not null)
        {
            filter += $" and RowKey gt '{lastRowKey}'";
        }

        var items = new List<RequestHistoryItem>(pageSize);
        var seenRecordIds = new HashSet<string>(StringComparer.Ordinal);
        string? lastExaminedRowKey = null;
        var hasMore = false;

        await foreach (var entity in _table.QueryAsync<RequestHistoryEntity>(
            filter,
            maxPerPage: pageSize,
            cancellationToken: cancellationToken))
        {
            lastExaminedRowKey = entity.RowKey;
            var recordId = entity.PrimaryRecordId;
            if (string.IsNullOrWhiteSpace(recordId) || !seenRecordIds.Add(recordId))
            {
                continue;
            }

            items.Add(Map(entity, null, null, null, null));
            if (items.Count == pageSize)
            {
                hasMore = true;
                break;
            }
        }

        var nextPageToken = hasMore && lastExaminedRowKey is not null
            ? EncodeContinuationToken(lastExaminedRowKey)
            : null;
        return new RequestHistoryPage(items, nextPageToken);
    }

    public async Task<RequestHistoryTimingAverages> GetTimingAveragesAsync(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        string rangeKey,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await EnsureRecentIndexReadyAsync(cancellationToken);

        if (IsTimingAverageCacheValid(rangeKey))
        {
            return _cachedTimingAverages!;
        }

        await _timingAveragesLock.WaitAsync(cancellationToken);
        try
        {
            if (IsTimingAverageCacheValid(rangeKey))
            {
                return _cachedTimingAverages!;
            }

            // History has multiple rows per batch as each processing leg completes; average
            // one sample per batch so later updates do not overweight a transaction.
            var batches = new Dictionary<string, BatchTimingValues>(StringComparer.Ordinal);
            await foreach (var entity in _table.QueryAsync<RequestHistoryEntity>(
                $"PartitionKey eq '{RecentPartitionKeyValue}'",
                maxPerPage: 1000,
                cancellationToken: cancellationToken))
            {
                var createdAtUtc = entity.CreatedAtUtc.ToUniversalTime();
                if (fromUtc is { } start && createdAtUtc < start.ToUniversalTime())
                {
                    continue;
                }

                if (toUtc is { } end && createdAtUtc > end.ToUniversalTime())
                {
                    continue;
                }

                var batchId = string.IsNullOrWhiteSpace(entity.BatchId)
                    ? entity.PrimaryRecordId ?? entity.RowKey
                    : entity.BatchId;
                if (!batches.TryGetValue(batchId, out var timings))
                {
                    timings = new BatchTimingValues();
                    batches.Add(batchId, timings);
                }

                timings.RtaPullMs ??= entity.RtaPullDurationMs;
                timings.TranslationMs ??= entity.TranslationDurationMs;
                timings.ApimRoundTripMs ??= entity.ApimRoundTripDurationMs;
                timings.RtaWritebackMs ??= entity.RtaWritebackDurationMs;
                timings.EndToEndMs ??= entity.EndToEndDurationMs;
                timings.RtaStatusPutFailed |=
                    string.Equals(entity.Status, "WritebackFailed", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(entity.Reason, "RtaStatusWritebackFailed", StringComparison.OrdinalIgnoreCase);
            }

            var calculated = new RequestHistoryTimingAverages(
                Average(batches.Values.Select(x => x.RtaPullMs)),
                Average(batches.Values.Select(x => x.TranslationMs)),
                Average(batches.Values.Select(x => x.ApimRoundTripMs)),
                Average(batches.Values.Select(x => x.RtaWritebackMs)),
                Average(batches.Values.Select(x => x.EndToEndMs)),
                batches.Values.LongCount(x => x.RtaStatusPutFailed),
                DateTimeOffset.UtcNow);
            _cachedTimingAverages = calculated;
            _cachedTimingAveragesRangeKey = rangeKey;
            return calculated;
        }
        finally
        {
            _timingAveragesLock.Release();
        }
    }

    private bool IsTimingAverageCacheValid(string rangeKey) =>
        _cachedTimingAverages is { } cached &&
        string.Equals(_cachedTimingAveragesRangeKey, rangeKey, StringComparison.Ordinal) &&
        DateTimeOffset.UtcNow - cached.CalculatedAtUtc < TimeSpan.FromMinutes(1);

    private static RequestHistoryTimingAverage Average(IEnumerable<long?> values)
    {
        var sum = 0d;
        long sampleCount = 0;
        foreach (var value in values)
        {
            if (value is not { } milliseconds)
            {
                continue;
            }

            sum += milliseconds;
            sampleCount++;
        }

        return new RequestHistoryTimingAverage(
            sampleCount == 0 ? null : sum / sampleCount,
            sampleCount);
    }

    public async Task<RequestHistoryItem?> GetAsync(
        string recordId,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        RequestHistoryEntity entity;
        try
        {
            entity = (await _table.GetEntityAsync<RequestHistoryEntity>(
                PartitionKeyValue,
                recordId,
                cancellationToken: cancellationToken)).Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }

        string? requestPayload = null;
        string? responsePayload = null;
        IReadOnlyList<string>? rtaResponsePayloads = null;
        string? rtaStatusPayload = null;

        if (_options.ShouldCaptureRequestHistoryPayloads())
        {
            requestPayload = await ReadPayloadAsync(entity.RequestPayloadBlob, cancellationToken);
            responsePayload = await ReadPayloadAsync(entity.ResponsePayloadBlob, cancellationToken);
            rtaResponsePayloads = await ReadRtaResponsePayloadsAsync(
                entity.RtaResponsePayloadBlobs,
                cancellationToken);
            rtaStatusPayload = await ReadPayloadAsync(entity.RtaStatusPayloadBlob, cancellationToken);
        }

        return Map(entity, requestPayload, responsePayload, rtaResponsePayloads, rtaStatusPayload);
    }

    private sealed class BatchTimingValues
    {
        public long? RtaPullMs { get; set; }
        public long? TranslationMs { get; set; }
        public long? ApimRoundTripMs { get; set; }
        public long? RtaWritebackMs { get; set; }
        public long? EndToEndMs { get; set; }
        public bool RtaStatusPutFailed { get; set; }
    }

    private RequestHistoryItem Map(
        RequestHistoryEntity entity,
        string? requestPayload,
        string? responsePayload,
        IReadOnlyList<string>? rtaResponsePayloads,
        string? rtaStatusPayload) =>
        new(
            entity.PrimaryRecordId ?? entity.RowKey,
            entity.BatchId,
            entity.UniqueId,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc,
            entity.Status,
            entity.Destination,
            entity.HttpStatusCode,
            entity.BusinessResponseCode,
            entity.Reason,
            entity.SentToApimAtUtc,
            entity.ApimResponseAtUtc,
            entity.BufferedToServiceBusAtUtc,
            _options.ShouldCaptureRequestHistoryPayloads(),
            _options.GetRequestHistoryPayloadMode(),
            requestPayload,
            responsePayload,
            rtaResponsePayloads,
            rtaStatusPayload,
            entity.RtaPullDurationMs,
            entity.TranslationDurationMs,
            entity.ApimRoundTripDurationMs,
            entity.RtaWritebackDurationMs,
            entity.EndToEndDurationMs);

    private async Task<string?> SaveRtaResponsePayloadsAsync(
        string batchId,
        IReadOnlyList<string> payloads,
        CancellationToken cancellationToken)
    {
        if (!_options.ShouldCaptureRequestHistoryPayloads() || payloads.Count == 0)
        {
            return null;
        }

        var blobNames = new List<string>(payloads.Count);
        for (var i = 0; i < payloads.Count; i++)
        {
            var blobName = await SavePayloadAsync(
                batchId,
                $"rta-response-{i + 1:D3}.json",
                payloads[i],
                cancellationToken);
            if (blobName is not null)
            {
                blobNames.Add(blobName);
            }
        }

        return blobNames.Count == 0 ? null : JsonSerializer.Serialize(blobNames);
    }

    private async Task<IReadOnlyList<string>?> ReadRtaResponsePayloadsAsync(
        string? blobNamesJson,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(blobNamesJson))
        {
            return null;
        }

        var blobNames = JsonSerializer.Deserialize<string[]>(blobNamesJson) ?? [];
        var payloads = await Task.WhenAll(blobNames.Select(
            blobName => ReadPayloadAsync(blobName, cancellationToken)));
        return payloads.Where(payload => payload is not null).Select(payload => payload!).ToArray();
    }

    private async Task<string?> SavePayloadAsync(
        string batchId,
        string fileName,
        string payload,
        CancellationToken cancellationToken)
    {
        if (!_options.ShouldCaptureRequestHistoryPayloads())
        {
            return null;
        }

        var blobName = $"{batchId}/{fileName}";
        var blob = _container.GetBlobClient(blobName);

        var payloadToStore = _options.RedactRequestHistoryPayload(payload);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(payloadToStore));
        await blob.UploadAsync(stream, overwrite: true, cancellationToken);
        return blobName;
    }

    private async Task<string?> ReadPayloadAsync(
        string? blobName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(blobName))
        {
            return null;
        }

        try
        {
            var response = await _container
                .GetBlobClient(blobName)
                .DownloadContentAsync(cancellationToken);

            var payload = response.Value.Content.ToString();
            return _options.RedactRequestHistoryPayload(payload);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    // Backfill the UI's recent-query partition once for existing history rows. The version
    // marker is written only after every batch succeeds, allowing an interrupted backfill to retry.
    private async Task EnsureRecentIndexReadyAsync(CancellationToken cancellationToken)
    {
        if (_recentIndexInitialized)
        {
            return;
        }

        await _recentIndexInitializeLock.WaitAsync(cancellationToken);
        try
        {
            if (_recentIndexInitialized)
            {
                return;
            }

            var marker = await _table.GetEntityIfExistsAsync<TableEntity>(
                IndexMetadataPartitionKey,
                IndexVersionRowKey,
                cancellationToken: cancellationToken);
            if (marker.HasValue)
            {
                _recentIndexInitialized = true;
                return;
            }

            var batch = new List<TableTransactionAction>(100);
            await foreach (var entity in _table.QueryAsync<RequestHistoryEntity>(
                x => x.PartitionKey == PartitionKeyValue,
                maxPerPage: 1000,
                cancellationToken: cancellationToken))
            {
                batch.Add(new TableTransactionAction(
                    TableTransactionActionType.UpsertReplace,
                    ToRecentIndexEntity(entity)));
                if (batch.Count == 100)
                {
                    await _table.SubmitTransactionAsync(batch, cancellationToken);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await _table.SubmitTransactionAsync(batch, cancellationToken);
            }

            await _table.UpsertEntityAsync(
                new TableEntity(IndexMetadataPartitionKey, IndexVersionRowKey)
                {
                    ["CreatedAtUtc"] = DateTimeOffset.UtcNow
                },
                TableUpdateMode.Replace,
                cancellationToken);
            _recentIndexInitialized = true;
        }
        finally
        {
            _recentIndexInitializeLock.Release();
        }
    }

    private async Task UpsertRecentIndexAsync(
        RequestHistoryEntity entity,
        string? previousIndexRowKey,
        CancellationToken cancellationToken)
    {
        var indexEntity = ToRecentIndexEntity(entity);
        await _table.UpsertEntityAsync(indexEntity, TableUpdateMode.Replace, cancellationToken);

        if (!string.IsNullOrWhiteSpace(previousIndexRowKey)
            && !string.Equals(previousIndexRowKey, indexEntity.RowKey, StringComparison.Ordinal))
        {
            try
            {
                await _table.DeleteEntityAsync(
                    RecentPartitionKeyValue,
                    previousIndexRowKey,
                    ETag.All,
                    cancellationToken);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // The index may still be backfilling older rows; the current entry is already written.
            }
        }
    }

    private static RequestHistoryEntity ToRecentIndexEntity(RequestHistoryEntity entity) =>
        new()
        {
            PartitionKey = RecentPartitionKeyValue,
            RowKey = BuildRecentIndexRowKey(entity.UpdatedAtUtc, entity.RowKey),
            PrimaryRecordId = entity.RowKey,
            BatchId = entity.BatchId,
            UniqueId = entity.UniqueId,
            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc,
            Status = entity.Status,
            Destination = entity.Destination,
            HttpStatusCode = entity.HttpStatusCode,
            BusinessResponseCode = entity.BusinessResponseCode,
            Reason = entity.Reason,
            SentToApimAtUtc = entity.SentToApimAtUtc,
            ApimResponseAtUtc = entity.ApimResponseAtUtc,
            BufferedToServiceBusAtUtc = entity.BufferedToServiceBusAtUtc,
            RtaPullDurationMs = entity.RtaPullDurationMs,
            TranslationDurationMs = entity.TranslationDurationMs,
            ApimRoundTripDurationMs = entity.ApimRoundTripDurationMs,
            RtaWritebackDurationMs = entity.RtaWritebackDurationMs,
            EndToEndDurationMs = entity.EndToEndDurationMs,
            PayloadLoggingEnabled = entity.PayloadLoggingEnabled
        };

    private static string BuildRecentIndexRowKey(DateTimeOffset updatedAtUtc, string recordId)
    {
        var reverseTicks = DateTime.MaxValue.Ticks - updatedAtUtc.UtcDateTime.Ticks;
        return $"{reverseTicks:D19}-{recordId}";
    }

    private static string? DecodeContinuationToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token));
            return System.Text.RegularExpressions.Regex.IsMatch(
                decoded,
                @"^\d{19}-[A-Za-z0-9-]+$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant)
                ? decoded
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string EncodeContinuationToken(string rowKey) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(rowKey));

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializeLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await _table.CreateIfNotExistsAsync(cancellationToken);
            await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    private static string BuildRowKey(string batchId, int index) =>
        $"{batchId}-{index:D4}";

    private sealed class RequestHistoryEntity : ITableEntity
    {
        public string PartitionKey { get; set; } = string.Empty;
        public string RowKey { get; set; } = string.Empty;
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }

        public string BatchId { get; set; } = string.Empty;
        public string UniqueId { get; set; } = string.Empty;
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
        public string Status { get; set; } = string.Empty;
        public string Destination { get; set; } = string.Empty;
        public int? HttpStatusCode { get; set; }
        public string? BusinessResponseCode { get; set; }
        public string? Reason { get; set; }
        public DateTimeOffset? SentToApimAtUtc { get; set; }
        public DateTimeOffset? ApimResponseAtUtc { get; set; }
        public DateTimeOffset? BufferedToServiceBusAtUtc { get; set; }
        public DateTimeOffset? PipelineStartedAtUtc { get; set; }
        public long? RtaPullDurationMs { get; set; }
        public long? TranslationDurationMs { get; set; }
        public long? ApimRoundTripDurationMs { get; set; }
        public long? RtaWritebackDurationMs { get; set; }
        public long? EndToEndDurationMs { get; set; }
        public bool PayloadLoggingEnabled { get; set; }
        public string? RequestPayloadBlob { get; set; }
        public string? ResponsePayloadBlob { get; set; }
        public string? RtaResponsePayloadBlobs { get; set; }
        public string? RtaStatusPayloadBlob { get; set; }
        public string? PrimaryRecordId { get; set; }
    }
}

