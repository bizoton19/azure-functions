using Azure;
using Azure.Data.Tables;

namespace StatusMonitor.Core.Models;

/// <summary>
/// A company that signed up for the product. PartitionKey is a constant because the
/// tenant directory is small; every other table is partitioned by tenant id.
/// </summary>
public sealed class TenantEntity : ITableEntity
{
    public const string Partition = "TENANT";

    public string PartitionKey { get; set; } = Partition;

    /// <summary>The tenant id, i.e. the Clerk/WorkOS organization id.</summary>
    public string RowKey { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string PlanId { get; set; } = string.Empty;
    public string Status { get; set; } = TenantStatus.Active;
    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }

    /// <summary>Semicolon-separated list of alert recipients.</summary>
    public string AlertEmails { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? LastPolledAtUtc { get; set; }

    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string TenantId => RowKey;
}

public static class TenantStatus
{
    public const string Active = "Active";

    /// <summary>Payment failed / past due — monitoring paused until resolved.</summary>
    public const string Suspended = "Suspended";

    public const string Cancelled = "Cancelled";
}

/// <summary>A URL a tenant wants monitored. PartitionKey = tenant id, RowKey = normalized url key.</summary>
public sealed class MonitoredUrlEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = string.Empty;

    public string UrlName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string TenantId => PartitionKey;
    public string UrlKey => RowKey;
}

/// <summary>The most recent check result for a URL. PartitionKey = tenant id, RowKey = url key.</summary>
public sealed class UrlStatusEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = string.Empty;

    public string UrlName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset CheckedAtUtc { get; set; }

    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
}

/// <summary>
/// Append-only history of every check. PartitionKey = tenant id; RowKey is
/// "{urlKey}_{reverseTicks}" so a prefix range scan returns one URL's history
/// in reverse chronological order without a full partition scan.
/// </summary>
public sealed class UrlStatusHistoryEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = string.Empty;

    public string UrlKey { get; set; } = string.Empty;
    public string UrlName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset CheckedAtUtc { get; set; }

    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public static string BuildRowKey(string urlKey, DateTimeOffset checkedAtUtc) =>
        $"{urlKey}_{long.MaxValue - checkedAtUtc.UtcTicks:D19}";
}
