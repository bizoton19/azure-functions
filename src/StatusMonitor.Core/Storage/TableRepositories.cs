using System.Runtime.CompilerServices;
using Azure;
using Azure.Data.Tables;
using StatusMonitor.Core.Models;
using StatusMonitor.Core.Tenancy;

namespace StatusMonitor.Core.Storage;

public sealed class TableTenantRepository(TableServiceClient tableService) : ITenantRepository
{
    private readonly TableClient _table = tableService.GetTableClient(TableNames.Tenants);

    public async Task<TenantEntity?> GetAsync(string tenantId, CancellationToken ct = default)
    {
        await _table.CreateIfNotExistsAsync(ct);
        var response = await _table.GetEntityIfExistsAsync<TenantEntity>(TenantEntity.Partition, tenantId, cancellationToken: ct);
        return response.HasValue ? response.Value : null;
    }

    public async Task<TenantEntity> GetOrCreateAsync(string tenantId, string name, string ownerEmail, CancellationToken ct = default)
    {
        var existing = await GetAsync(tenantId, ct);
        if (existing is not null)
        {
            return existing;
        }

        var tenant = new TenantEntity
        {
            RowKey = tenantId,
            Name = name,
            PlanId = PlanCatalog.FreePlanId,
            Status = TenantStatus.Active,
            AlertEmails = ownerEmail,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        try
        {
            await _table.AddEntityAsync(tenant, ct);
            return tenant;
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            // Lost a provisioning race with a concurrent request from the same tenant.
            return (await GetAsync(tenantId, ct))!;
        }
    }

    public async Task UpsertAsync(TenantEntity tenant, CancellationToken ct = default)
    {
        await _table.CreateIfNotExistsAsync(ct);
        await _table.UpsertEntityAsync(tenant, TableUpdateMode.Replace, ct);
    }

    public async IAsyncEnumerable<TenantEntity> ListActiveAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await _table.CreateIfNotExistsAsync(ct);
        var filter = $"PartitionKey eq '{TenantEntity.Partition}' and Status eq '{TenantStatus.Active}'";
        await foreach (var tenant in _table.QueryAsync<TenantEntity>(filter, cancellationToken: ct))
        {
            yield return tenant;
        }
    }
}

public sealed class TableMonitoredUrlRepository(TableServiceClient tableService) : IMonitoredUrlRepository
{
    private readonly TableClient _table = tableService.GetTableClient(TableNames.MonitoredUrls);

    public async Task<IReadOnlyList<MonitoredUrlEntity>> ListAsync(string tenantId, CancellationToken ct = default)
    {
        await _table.CreateIfNotExistsAsync(ct);
        var results = new List<MonitoredUrlEntity>();
        await foreach (var entity in _table.QueryAsync<MonitoredUrlEntity>(
            e => e.PartitionKey == tenantId, cancellationToken: ct))
        {
            results.Add(entity);
        }

        return results;
    }

    public async Task<int> CountAsync(string tenantId, CancellationToken ct = default)
    {
        await _table.CreateIfNotExistsAsync(ct);
        var count = 0;
        await foreach (var _ in _table.QueryAsync<MonitoredUrlEntity>(
            e => e.PartitionKey == tenantId, select: ["RowKey"], cancellationToken: ct))
        {
            count++;
        }

        return count;
    }

    public async Task UpsertAsync(MonitoredUrlEntity url, CancellationToken ct = default)
    {
        await _table.CreateIfNotExistsAsync(ct);
        await _table.UpsertEntityAsync(url, TableUpdateMode.Replace, ct);
    }

    public async Task DeleteAsync(string tenantId, string urlKey, CancellationToken ct = default)
    {
        await _table.CreateIfNotExistsAsync(ct);
        await _table.DeleteEntityAsync(tenantId, urlKey, ETag.All, ct);
    }
}

public sealed class TableUrlStatusRepository(TableServiceClient tableService) : IUrlStatusRepository
{
    private readonly TableClient _current = tableService.GetTableClient(TableNames.UrlStatuses);
    private readonly TableClient _history = tableService.GetTableClient(TableNames.UrlStatusHistory);

    public async Task<IReadOnlyList<UrlStatusEntity>> ListCurrentAsync(string tenantId, CancellationToken ct = default)
    {
        await _current.CreateIfNotExistsAsync(ct);
        var results = new List<UrlStatusEntity>();
        await foreach (var entity in _current.QueryAsync<UrlStatusEntity>(
            e => e.PartitionKey == tenantId, cancellationToken: ct))
        {
            results.Add(entity);
        }

        return results;
    }

    public async Task<UrlStatusEntity?> GetCurrentAsync(string tenantId, string urlKey, CancellationToken ct = default)
    {
        await _current.CreateIfNotExistsAsync(ct);
        var response = await _current.GetEntityIfExistsAsync<UrlStatusEntity>(tenantId, urlKey, cancellationToken: ct);
        return response.HasValue ? response.Value : null;
    }

    public async Task UpsertCurrentAsync(UrlCheckResult result, CancellationToken ct = default)
    {
        await _current.CreateIfNotExistsAsync(ct);
        var entity = new UrlStatusEntity
        {
            PartitionKey = result.TenantId,
            RowKey = result.UrlKey,
            UrlName = result.UrlName,
            Url = result.Url,
            Status = result.Status,
            Description = result.Description,
            CheckedAtUtc = result.CheckedAtUtc,
        };
        await _current.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public async Task AppendHistoryAsync(UrlCheckResult result, CancellationToken ct = default)
    {
        await _history.CreateIfNotExistsAsync(ct);
        var entity = new UrlStatusHistoryEntity
        {
            PartitionKey = result.TenantId,
            RowKey = UrlStatusHistoryEntity.BuildRowKey(result.UrlKey, result.CheckedAtUtc),
            UrlKey = result.UrlKey,
            UrlName = result.UrlName,
            Url = result.Url,
            Status = result.Status,
            Description = result.Description,
            CheckedAtUtc = result.CheckedAtUtc,
        };
        await _history.AddEntityAsync(entity, ct);
    }

    public async Task<IReadOnlyList<UrlStatusHistoryEntity>> ListHistoryAsync(
        string tenantId, string urlKey, int take, CancellationToken ct = default)
    {
        await _history.CreateIfNotExistsAsync(ct);

        // Reverse-tick row keys make "RowKey >= '{urlKey}_' and RowKey < '{urlKey}`'" a
        // contiguous, newest-first range for this URL ('`' is the char after '_').
        var lower = $"{urlKey}_";
        var upper = $"{urlKey}`";
        var filter = $"PartitionKey eq '{tenantId}' and RowKey ge '{lower}' and RowKey lt '{upper}'";

        var results = new List<UrlStatusHistoryEntity>(take);
        await foreach (var entity in _history.QueryAsync<UrlStatusHistoryEntity>(filter, maxPerPage: take, cancellationToken: ct))
        {
            results.Add(entity);
            if (results.Count >= take)
            {
                break;
            }
        }

        return results;
    }
}
