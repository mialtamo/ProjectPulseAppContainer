using Azure;
using Azure.Data.Tables;
using ProjectPulse.Processor.Models;
using ProjectPulse.Processor.Options;

namespace ProjectPulse.Processor.Services;

public sealed class AzureStoragePollBatchLedger : IPollBatchLedger
{
    private const string BatchPartition = "!pollruns";
    private const string BatchIndexPartition = "!pollindex";
    private const string RecordPartition = "!pollitems";
    private readonly TableClient _table;

    public AzureStoragePollBatchLedger(IOptions<ClaimProcessorOptions> options)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("RequestHistoryStorageConnectionString")
            ?? Environment.GetEnvironmentVariable("AzureWebJobsStorage")
            ?? throw new InvalidOperationException(
                "Poll batch reconciliation requires RequestHistoryStorageConnectionString or AzureWebJobsStorage.");
        _table = new TableClient(connectionString, options.Value.RequestHistoryTableName);
    }

    public async Task RecordReceivedAsync(
        string pollRunId,
        string source,
        DateTimeOffset receivedAtUtc,
        long? rtaPullDurationMs,
        bool rtaWritebackEnabled,
        IReadOnlyList<PollBatchRecordSeed> records,
        CancellationToken cancellationToken)
    {
        await _table.CreateIfNotExistsAsync(cancellationToken);
        var batch = new TableEntity(BatchPartition, pollRunId)
        {
            ["PollRunId"] = pollRunId,
            ["Source"] = source,
            ["ReceivedAtUtc"] = receivedAtUtc,
            ["RtaPullDurationMs"] = rtaPullDurationMs,
            ["RtaWritebackEnabled"] = rtaWritebackEnabled,
            ["ReceivedCount"] = records.Count
        };
        await _table.UpsertEntityAsync(batch, TableUpdateMode.Replace, cancellationToken);

        var indexKey = $"{DateTimeOffset.MaxValue.UtcTicks - receivedAtUtc.UtcTicks:D19}-{pollRunId}";
        await _table.UpsertEntityAsync(new TableEntity(BatchIndexPartition, indexKey)
        {
            ["PollRunId"] = pollRunId,
            ["ReceivedAtUtc"] = receivedAtUtc
        }, TableUpdateMode.Replace, cancellationToken);

        foreach (var record in records)
        {
            var item = new TableEntity(RecordPartition, BuildRecordKey(pollRunId, record.Ordinal))
            {
                ["PollRunId"] = pollRunId,
                ["Ordinal"] = record.Ordinal,
                ["UniqueId"] = record.UniqueId,
                ["RtaRequestId"] = record.RtaRequestId,
                ["ReceivedAtUtc"] = receivedAtUtc,
                ["ProcessingStage"] = "Received"
            };
            await _table.UpsertEntityAsync(item, TableUpdateMode.Replace, cancellationToken);
        }
    }

    public Task RecordApimAttemptAsync(
        string pollRunId,
        int ordinal,
        DateTimeOffset attemptedAtUtc,
        CancellationToken cancellationToken) =>
        UpdateRecordAsync(pollRunId, ordinal, entity =>
        {
            entity["ApimAttemptedAtUtc"] = attemptedAtUtc;
            entity["ProcessingStage"] = "ApimAttempted";
        }, cancellationToken);

    public Task RecordApimResponseAsync(
        string pollRunId,
        int ordinal,
        int httpStatusCode,
        DateTimeOffset responseAtUtc,
        CancellationToken cancellationToken) =>
        UpdateRecordAsync(pollRunId, ordinal, entity =>
        {
            entity["ApimResponseAtUtc"] = responseAtUtc;
            entity["ApimHttpStatusCode"] = httpStatusCode;
            entity["ProcessingStage"] = "ApimResponded";
        }, cancellationToken);

    public Task RecordServiceBusBufferedAsync(
        string pollRunId,
        int ordinal,
        string reason,
        DateTimeOffset bufferedAtUtc,
        CancellationToken cancellationToken) =>
        UpdateRecordAsync(pollRunId, ordinal, entity =>
        {
            entity["ServiceBusBufferedAtUtc"] = bufferedAtUtc;
            entity["ServiceBusReason"] = Truncate(reason);
            entity["ProcessingStage"] = "Buffered";
        }, cancellationToken);

    public Task RecordRtaWritebackAsync(
        string pollRunId,
        int ordinal,
        bool? succeeded,
        int? httpStatusCode,
        string? reason,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken) =>
        UpdateRecordAsync(pollRunId, ordinal, entity =>
        {
            entity["RtaWritebackAtUtc"] = completedAtUtc;
            entity["RtaWritebackSucceeded"] = succeeded;
            entity["RtaWritebackHttpStatusCode"] = httpStatusCode;
            entity["RtaWritebackReason"] = Truncate(reason);
            entity["ProcessingStage"] = succeeded is null ? "RtaWritebackPending" : succeeded.Value ? "RtaWritebackSucceeded" : "RtaWritebackFailed";
        }, cancellationToken);

    public Task CompletePollAsync(
        string pollRunId,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken) =>
        UpdateBatchAsync(pollRunId, entity => entity["CompletedAtUtc"] = completedAtUtc, cancellationToken);

    public async Task<IReadOnlyList<PollBatchSummary>> ListAsync(
        int limit,
        DateTimeOffset? receivedAfterUtc,
        CancellationToken cancellationToken)
    {
        await _table.CreateIfNotExistsAsync(cancellationToken);
        var indexes = new List<TableEntity>();
        await foreach (var index in _table.QueryAsync<TableEntity>(
            x => x.PartitionKey == BatchIndexPartition,
            maxPerPage: 1000,
            cancellationToken: cancellationToken))
        {
            var receivedAt = index.GetDateTimeOffset("ReceivedAtUtc");
            if (receivedAfterUtc is { } cutoff && (receivedAt is null || receivedAt < cutoff))
            {
                continue;
            }
            indexes.Add(index);
        }

        var summaries = new List<PollBatchSummary>();
        foreach (var index in indexes.OrderBy(x => x.RowKey, StringComparer.Ordinal).Take(Math.Clamp(limit, 1, 500)))
        {
            var pollRunId = index.GetString("PollRunId");
            if (string.IsNullOrWhiteSpace(pollRunId))
            {
                continue;
            }
            var detail = await GetAsync(pollRunId, cancellationToken);
            if (detail is not null)
            {
                summaries.Add(detail.Summary);
            }
        }

        return summaries;
    }

    public async Task<PollBatchDetail?> GetAsync(
        string pollRunId,
        CancellationToken cancellationToken)
    {
        await _table.CreateIfNotExistsAsync(cancellationToken);
        TableEntity batch;
        try
        {
            batch = (await _table.GetEntityAsync<TableEntity>(
                BatchPartition,
                pollRunId,
                cancellationToken: cancellationToken)).Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }

        var records = new List<PollBatchRecord>();
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {RecordPartition} and PollRunId eq {pollRunId}");
        await foreach (var entity in _table.QueryAsync<TableEntity>(
            filter,
            maxPerPage: 1000,
            cancellationToken: cancellationToken))
        {
            records.Add(new PollBatchRecord(
                entity.GetInt32("Ordinal") ?? 0,
                entity.GetString("UniqueId") ?? string.Empty,
                entity.GetString("RtaRequestId"),
                entity.GetDateTimeOffset("ReceivedAtUtc") ?? DateTimeOffset.MinValue,
                entity.GetDateTimeOffset("ApimAttemptedAtUtc"),
                entity.GetDateTimeOffset("ApimResponseAtUtc"),
                entity.GetInt32("ApimHttpStatusCode"),
                entity.GetDateTimeOffset("ServiceBusBufferedAtUtc"),
                entity.GetString("ServiceBusReason"),
                entity.GetDateTimeOffset("RtaWritebackAtUtc"),
                entity.GetBoolean("RtaWritebackSucceeded"),
                entity.GetInt32("RtaWritebackHttpStatusCode"),
                entity.GetString("RtaWritebackReason"),
                entity.GetString("ProcessingDisposition"),
                entity.GetDateTimeOffset("ProcessingCompletedAtUtc")));
        }

        records.Sort((left, right) => left.Ordinal.CompareTo(right.Ordinal));
        var writebackEnabled = batch.GetBoolean("RtaWritebackEnabled") ?? true;
        var summary = new PollBatchSummary(
            pollRunId,
            batch.GetString("Source") ?? "Unknown",
            batch.GetDateTimeOffset("ReceivedAtUtc") ?? DateTimeOffset.MinValue,
            batch.GetDateTimeOffset("CompletedAtUtc"),
            batch.GetInt64("RtaPullDurationMs"),
            records.Count,
            records.Count(x => x.ApimAttemptedAtUtc is not null),
            records.Count(x => x.ApimResponseAtUtc is not null),
            records.Count(x => x.ServiceBusBufferedAtUtc is not null),
            records.Count(x => x.RtaWritebackSucceeded == true),
            records.Count(x => x.RtaWritebackSucceeded == false),
            writebackEnabled ? records.Count(x => x.RtaWritebackSucceeded is null) : 0);

        return new PollBatchDetail(summary, records);
    }

    public async Task<int> DeleteForCleanupAsync(
        DateTimeOffset? receivedAfterUtc,
        CancellationToken cancellationToken)
    {
        await _table.CreateIfNotExistsAsync(cancellationToken);
        var pollRunIds = new HashSet<string>(StringComparer.Ordinal);
        var entities = new List<TableEntity>();

        await foreach (var batch in _table.QueryAsync<TableEntity>(
            x => x.PartitionKey == BatchPartition,
            maxPerPage: 1000,
            cancellationToken: cancellationToken))
        {
            var receivedAtUtc = batch.GetDateTimeOffset("ReceivedAtUtc");
            if (receivedAfterUtc is null || receivedAtUtc is null || receivedAtUtc >= receivedAfterUtc.Value)
            {
                pollRunIds.Add(batch.RowKey);
                entities.Add(new TableEntity(batch.PartitionKey, batch.RowKey) { ETag = ETag.All });
            }
        }

        if (pollRunIds.Count == 0)
        {
            return 0;
        }

        await foreach (var record in _table.QueryAsync<TableEntity>(
            x => x.PartitionKey == RecordPartition,
            maxPerPage: 1000,
            cancellationToken: cancellationToken))
        {
            if (pollRunIds.Contains(record.GetString("PollRunId") ?? string.Empty))
            {
                entities.Add(new TableEntity(record.PartitionKey, record.RowKey) { ETag = ETag.All });
            }
        }

        await foreach (var index in _table.QueryAsync<TableEntity>(
            x => x.PartitionKey == BatchIndexPartition,
            maxPerPage: 1000,
            cancellationToken: cancellationToken))
        {
            if (pollRunIds.Contains(index.GetString("PollRunId") ?? string.Empty))
            {
                entities.Add(new TableEntity(index.PartitionKey, index.RowKey) { ETag = ETag.All });
            }
        }

        foreach (var partition in entities.GroupBy(x => x.PartitionKey))
        {
            var batch = new List<TableTransactionAction>(100);
            foreach (var entity in partition)
            {
                batch.Add(new TableTransactionAction(TableTransactionActionType.Delete, entity));
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
        }

        return entities.Count;
    }

    private async Task UpdateRecordAsync(
        string pollRunId,
        int ordinal,
        Action<TableEntity> update,
        CancellationToken cancellationToken)
    {
        var entity = new TableEntity(RecordPartition, BuildRecordKey(pollRunId, ordinal));
        update(entity);
        try
        {
            await _table.UpdateEntityAsync(entity, ETag.All, TableUpdateMode.Merge, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }
    }

    private async Task UpdateBatchAsync(
        string pollRunId,
        Action<TableEntity> update,
        CancellationToken cancellationToken)
    {
        try
        {
            var entity = (await _table.GetEntityAsync<TableEntity>(
                BatchPartition,
                pollRunId,
                cancellationToken: cancellationToken)).Value;
            update(entity);
            await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }
    }

    private static string BuildRecordKey(string pollRunId, int ordinal) =>
        $"{pollRunId}-{ordinal:D6}";

    private static string? Truncate(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= 500 ? value : value[..500];
}
