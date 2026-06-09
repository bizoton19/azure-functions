namespace StatusMonitor.Core.Tenancy;

/// <summary>
/// The resolved identity of the calling company (tenant) for the current request.
/// Every storage read/write is scoped by <see cref="TenantId"/> so that data from
/// different companies can never bleed across partition boundaries.
/// </summary>
public sealed record TenantContext(string TenantId, string UserId)
{
    public static TenantContext System(string tenantId) => new(tenantId, "system");
}
