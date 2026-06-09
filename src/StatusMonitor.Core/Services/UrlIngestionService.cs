using Microsoft.Extensions.Logging;
using StatusMonitor.Core.Models;
using StatusMonitor.Core.Storage;
using StatusMonitor.Core.Tenancy;

namespace StatusMonitor.Core.Services;

public sealed record IngestionResult(
    bool Accepted,
    int QueuedCount,
    int CurrentCount,
    int MaxUrls,
    IReadOnlyList<string> Errors);

/// <summary>
/// Validates a bulk URL submission against the tenant's plan limits and, when
/// allowed, enqueues it for asynchronous persistence. Writes go through the
/// queue so a 5,000-row spreadsheet upload returns to the SPA immediately.
/// </summary>
public sealed class UrlIngestionService(
    IMonitoredUrlRepository urlRepository,
    IQueuePublisher queuePublisher,
    ILogger<UrlIngestionService> logger)
{
    /// <summary>Ingest raw CSV / pasted text, e.g. a spreadsheet upload from the SPA.</summary>
    public Task<IngestionResult> IngestRawAsync(
        TenantEntity tenant, string rawContent, CancellationToken ct = default) =>
        IngestAsync(tenant, UrlListParser.Parse(rawContent), ct);

    public async Task<IngestionResult> IngestAsync(
        TenantEntity tenant, UrlParseOutcome outcome, CancellationToken ct = default)
    {
        var plan = PlanCatalog.Resolve(tenant.PlanId);

        if (outcome.Urls.Count == 0)
        {
            var errors = outcome.Errors.Count > 0
                ? outcome.Errors
                : ["No valid URLs were found in the submitted content."];
            return new IngestionResult(false, 0, 0, plan.MaxUrls, errors);
        }

        var existing = await urlRepository.ListAsync(tenant.TenantId, ct);
        var existingKeys = existing.Select(e => e.UrlKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newCount = outcome.Urls.Count(u => !existingKeys.Contains(UrlKey.FromName(u.UrlName)));
        var projectedTotal = existing.Count + newCount;

        if (projectedTotal > plan.MaxUrls)
        {
            return new IngestionResult(
                Accepted: false,
                QueuedCount: 0,
                CurrentCount: existing.Count,
                MaxUrls: plan.MaxUrls,
                Errors:
                [
                    $"This import would bring you to {projectedTotal} monitored URLs, but the " +
                    $"{plan.Name} plan allows {plan.MaxUrls}. Remove some URLs or upgrade your plan.",
                ]);
        }

        var items = outcome.Urls
            .Select(u => new UrlImportItem(UrlImportActions.Upsert, UrlKey.FromName(u.UrlName), u.UrlName, u.Url))
            .ToList();

        await queuePublisher.PublishAsync(QueueNames.UrlImports, new UrlImportMessage(tenant.TenantId, items), ct);

        logger.LogInformation(
            "Tenant {TenantId} queued {Count} URLs for import ({ErrorCount} rejected rows)",
            tenant.TenantId, items.Count, outcome.Errors.Count);

        return new IngestionResult(true, items.Count, existing.Count, plan.MaxUrls, outcome.Errors);
    }

    public async Task DeleteAsync(TenantEntity tenant, string urlKey, CancellationToken ct = default)
    {
        var message = new UrlImportMessage(
            tenant.TenantId,
            [new UrlImportItem(UrlImportActions.Delete, urlKey, urlKey, string.Empty)]);
        await queuePublisher.PublishAsync(QueueNames.UrlImports, message, ct);
    }
}
