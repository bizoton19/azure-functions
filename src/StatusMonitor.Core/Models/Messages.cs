namespace StatusMonitor.Core.Models;

/// <summary>Instructs the poller to run a check cycle for one tenant.</summary>
public sealed record PollJobMessage(string TenantId);

/// <summary>The outcome of checking a single URL.</summary>
public sealed record UrlCheckResult(
    string TenantId,
    string UrlKey,
    string UrlName,
    string Url,
    string Status,
    string Description,
    DateTimeOffset CheckedAtUtc)
{
    public bool IsHealthy => Status == KnownStatuses.Ok;
}

public static class KnownStatuses
{
    public const string Ok = "OK";
    public const string Degraded = "Degraded";
    public const string Unreachable = "Unreachable";
}

/// <summary>All failures detected in one polling run for a tenant; drives a single alert email.</summary>
public sealed record AlertMessage(string TenantId, IReadOnlyList<UrlCheckResult> Failures);

public static class UrlImportActions
{
    public const string Upsert = "Upsert";
    public const string Delete = "Delete";
}

public sealed record UrlImportItem(string Action, string UrlKey, string UrlName, string Url);

/// <summary>A batch of URL changes (spreadsheet upload, pasted list, or single delete).</summary>
public sealed record UrlImportMessage(string TenantId, IReadOnlyList<UrlImportItem> Items);
