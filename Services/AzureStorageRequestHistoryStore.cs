using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using ProjectPulse.Processor.Models;
using ProjectPulse.Processor.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProjectPulse.Processor.Services;

public sealed class AzureStorageRequestHistoryStore : IRequestHistoryStore
{
    private const string PartitionKeyValue = "requests";

    private readonly TableClient _table;
    private readonly BlobContainerClient _container;
    private readonly ClaimProcessorOptions _options;
    private readonly ILogger<AzureStorageRequestHistoryStore> _logger;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

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
        string requestPayload,
        DateTimeOffset sentAtUtc,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var requestBlob = await SavePayloadAsync(
            batchId,
            "request.json",
            requestPayload,
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
                RequestPayloadBlob = requestBlob,
                PayloadLoggingEnabled = _options.ShouldLogPollPayloads()
            };

            await _table.UpsertEntityAsync(entity, TableUpdateMode.Merge, cancellationToken);
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
        CancellationToken cancellationToken)
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
                    CreatedAtUtc = completedAtUtc
                };
            }

            entity.UpdatedAtUtc = completedAtUtc;
            entity.Status = status;
            entity.Destination = sentToServiceBus ? "ServiceBus" : "APIM";
            entity.HttpStatusCode = httpStatusCode;
            entity.BusinessResponseCode = businessResponseCode;
            entity.Reason = reason;
            entity.ApimResponseAtUtc = completedAtUtc;
            entity.ResponsePayloadBlob = responseBlob;
            entity.PayloadLoggingEnabled = _options.ShouldLogPollPayloads();

            if (sentToServiceBus)
            {
                entity.BufferedToServiceBusAtUtc = completedAtUtc;
            }

            await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken);
        }
    }

    public async Task RecordServiceBusAsync(
        string batchId,
        IReadOnlyList<string> uniqueIds,
        string reason,
        DateTimeOffset bufferedAtUtc,
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
            entity.PayloadLoggingEnabled = _options.ShouldLogPollPayloads();

            await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<RequestHistoryItem>> ListAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        var results = new List<RequestHistoryEntity>();

        await foreach (var entity in _table.QueryAsync<RequestHistoryEntity>(
            x => x.PartitionKey == PartitionKeyValue,
            maxPerPage: 1000,
            cancellationToken: cancellationToken))
        {
            results.Add(entity);
        }

        return results
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Take(Math.Clamp(limit, 1, 1000))
            .Select(x => Map(x, null, null))
            .ToArray();
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

        if (_options.ShouldLogPollPayloads())
        {
            requestPayload = await ReadPayloadAsync(entity.RequestPayloadBlob, cancellationToken);
            responsePayload = await ReadPayloadAsync(entity.ResponsePayloadBlob, cancellationToken);
        }

        return Map(entity, requestPayload, responsePayload);
    }

    private RequestHistoryItem Map(
        RequestHistoryEntity entity,
        string? requestPayload,
        string? responsePayload) =>
        new(
            entity.RowKey,
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
            _options.ShouldLogPollPayloads(),
            requestPayload,
            responsePayload);

    private async Task<string?> SavePayloadAsync(
        string batchId,
        string fileName,
        string payload,
        CancellationToken cancellationToken)
    {
        if (!_options.ShouldLogPollPayloads())
        {
            return null;
        }

        var blobName = $"{batchId}/{fileName}";
        var blob = _container.GetBlobClient(blobName);

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(payload));
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

            return response.Value.Content.ToString();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

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
        public bool PayloadLoggingEnabled { get; set; }
        public string? RequestPayloadBlob { get; set; }
        public string? ResponsePayloadBlob { get; set; }
    }
}
