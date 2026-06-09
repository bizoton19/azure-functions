using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using StatusMonitor.Core.Models;
using StatusMonitor.Core.Storage;

namespace StatusMonitor.Functions.Functions;

/// <summary>Replaces the legacy statusQueuePersister: upserts the latest state per URL.</summary>
public sealed class StatusStatePersisterFunction(IUrlStatusRepository statuses)
{
    [Function("StatusStatePersister")]
    public async Task Run(
        [QueueTrigger(QueueNames.StatusStates, Connection = "AzureWebJobsStorage")] string message,
        CancellationToken ct)
    {
        var result = Json.Deserialize<UrlCheckResult>(message);
        await statuses.UpsertCurrentAsync(result, ct);
    }
}

/// <summary>Replaces the legacy statusHistoryQueuePersister: appends every check to the history table.</summary>
public sealed class StatusHistoryPersisterFunction(IUrlStatusRepository statuses)
{
    [Function("StatusHistoryPersister")]
    public async Task Run(
        [QueueTrigger(QueueNames.StatusHistory, Connection = "AzureWebJobsStorage")] string message,
        CancellationToken ct)
    {
        var result = Json.Deserialize<UrlCheckResult>(message);
        await statuses.AppendHistoryAsync(result, ct);
    }
}

/// <summary>
/// Replaces the legacy urlQueuePersister: applies queued URL imports (spreadsheet
/// uploads, pasted lists) and deletions to the MonitoredUrls table.
/// </summary>
public sealed class UrlImportPersisterFunction(
    IMonitoredUrlRepository urls,
    ILogger<UrlImportPersisterFunction> logger)
{
    [Function("UrlImportPersister")]
    public async Task Run(
        [QueueTrigger(QueueNames.UrlImports, Connection = "AzureWebJobsStorage")] string message,
        CancellationToken ct)
    {
        var import = Json.Deserialize<UrlImportMessage>(message);
        var now = DateTimeOffset.UtcNow;

        foreach (var item in import.Items)
        {
            switch (item.Action)
            {
                case UrlImportActions.Upsert:
                    await urls.UpsertAsync(new MonitoredUrlEntity
                    {
                        PartitionKey = import.TenantId,
                        RowKey = item.UrlKey,
                        UrlName = item.UrlName,
                        Url = item.Url,
                        IsActive = true,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                    }, ct);
                    break;

                case UrlImportActions.Delete:
                    await urls.DeleteAsync(import.TenantId, item.UrlKey, ct);
                    break;

                default:
                    logger.LogWarning(
                        "Tenant {TenantId}: unknown import action '{Action}' for {UrlKey}",
                        import.TenantId, item.Action, item.UrlKey);
                    break;
            }
        }

        logger.LogInformation(
            "Tenant {TenantId}: applied {Count} URL changes", import.TenantId, import.Items.Count);
    }
}
