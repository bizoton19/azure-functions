using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using StatusMonitor.Core.Models;
using StatusMonitor.Core.Services;
using StatusMonitor.Core.Storage;

namespace StatusMonitor.Functions.Functions;

/// <summary>
/// Replaces the legacy statusPoller_http function. Consumes one poll job per
/// tenant, checks that tenant's URLs, and fans results out to the state,
/// history, and (when something is down) notification queues.
/// </summary>
public sealed class UrlPollerFunction(
    IMonitoredUrlRepository urls,
    UrlPollerService poller,
    IQueuePublisher queuePublisher,
    ILogger<UrlPollerFunction> logger)
{
    [Function("UrlPoller")]
    public async Task Run(
        [QueueTrigger(QueueNames.PollJobs, Connection = "AzureWebJobsStorage")] string message,
        CancellationToken ct)
    {
        var job = Json.Deserialize<PollJobMessage>(message);
        var monitored = (await urls.ListAsync(job.TenantId, ct)).Where(u => u.IsActive).ToList();
        if (monitored.Count == 0)
        {
            return;
        }

        var results = await poller.PollAsync(job.TenantId, monitored, ct);

        foreach (var result in results)
        {
            await queuePublisher.PublishAsync(QueueNames.StatusStates, result, ct);
            await queuePublisher.PublishAsync(QueueNames.StatusHistory, result, ct);
        }

        var failures = results.Where(r => !r.IsHealthy).ToList();
        if (failures.Count > 0)
        {
            await queuePublisher.PublishAsync(
                QueueNames.StatusNotifications, new AlertMessage(job.TenantId, failures), ct);
        }

        logger.LogInformation(
            "Tenant {TenantId}: polled {Total} URLs, {Failures} failing",
            job.TenantId, results.Count, failures.Count);
    }
}
