using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using StatusMonitor.Core.Models;
using StatusMonitor.Core.Storage;
using StatusMonitor.Core.Tenancy;

namespace StatusMonitor.Functions.Functions;

/// <summary>
/// Replaces the legacy pollerTrigger timer. Instead of one global hourly poll,
/// it ticks every minute, finds tenants whose plan interval has elapsed, and
/// fans out one poll job per tenant — so paid tiers get tighter intervals and
/// the work scales horizontally across queue consumers.
/// </summary>
public sealed class PollSchedulerFunction(
    ITenantRepository tenants,
    IQueuePublisher queuePublisher,
    ILogger<PollSchedulerFunction> logger)
{
    [Function("PollScheduler")]
    public async Task Run([TimerTrigger("0 */1 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var scheduled = 0;

        await foreach (var tenant in tenants.ListActiveAsync(ct))
        {
            var plan = PlanCatalog.Resolve(tenant.PlanId);
            var due = tenant.LastPolledAtUtc is null ||
                      now - tenant.LastPolledAtUtc >= TimeSpan.FromMinutes(plan.PollIntervalMinutes);
            if (!due)
            {
                continue;
            }

            await queuePublisher.PublishAsync(QueueNames.PollJobs, new PollJobMessage(tenant.TenantId), ct);
            tenant.LastPolledAtUtc = now;
            await tenants.UpsertAsync(tenant, ct);
            scheduled++;
        }

        if (scheduled > 0)
        {
            logger.LogInformation("Scheduled poll jobs for {Count} tenants", scheduled);
        }
    }
}
