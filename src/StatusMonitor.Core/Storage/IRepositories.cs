using StatusMonitor.Core.Models;

namespace StatusMonitor.Core.Storage;

public interface ITenantRepository
{
    Task<TenantEntity?> GetAsync(string tenantId, CancellationToken ct = default);
    Task<TenantEntity> GetOrCreateAsync(string tenantId, string name, string ownerEmail, CancellationToken ct = default);
    Task UpsertAsync(TenantEntity tenant, CancellationToken ct = default);
    IAsyncEnumerable<TenantEntity> ListActiveAsync(CancellationToken ct = default);
}

public interface IMonitoredUrlRepository
{
    Task<IReadOnlyList<MonitoredUrlEntity>> ListAsync(string tenantId, CancellationToken ct = default);
    Task<int> CountAsync(string tenantId, CancellationToken ct = default);
    Task UpsertAsync(MonitoredUrlEntity url, CancellationToken ct = default);
    Task DeleteAsync(string tenantId, string urlKey, CancellationToken ct = default);
}

public interface IUrlStatusRepository
{
    Task<IReadOnlyList<UrlStatusEntity>> ListCurrentAsync(string tenantId, CancellationToken ct = default);
    Task<UrlStatusEntity?> GetCurrentAsync(string tenantId, string urlKey, CancellationToken ct = default);
    Task UpsertCurrentAsync(UrlCheckResult result, CancellationToken ct = default);
    Task AppendHistoryAsync(UrlCheckResult result, CancellationToken ct = default);
    Task<IReadOnlyList<UrlStatusHistoryEntity>> ListHistoryAsync(
        string tenantId, string urlKey, int take, CancellationToken ct = default);
}
