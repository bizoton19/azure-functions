namespace StatusMonitor.Core.Storage;

public static class TableNames
{
    public const string Tenants = "Tenants";
    public const string MonitoredUrls = "MonitoredUrls";
    public const string UrlStatuses = "UrlStatuses";
    public const string UrlStatusHistory = "UrlStatusHistory";
}

public static class QueueNames
{
    /// <summary>Fan-out: one message per tenant due for a polling run.</summary>
    public const string PollJobs = "poll-jobs-queue";

    /// <summary>Latest state of a single URL check (upserted into UrlStatuses).</summary>
    public const string StatusStates = "status-states-queue";

    /// <summary>Append-only copy of every URL check (inserted into UrlStatusHistory).</summary>
    public const string StatusHistory = "status-history-queue";

    /// <summary>Aggregated failures for a tenant's polling run; consumed by the email alerter.</summary>
    public const string StatusNotifications = "status-notifications-queue";

    /// <summary>Bulk URL imports (spreadsheet upload / pasted list) awaiting persistence.</summary>
    public const string UrlImports = "url-import-queue";
}
