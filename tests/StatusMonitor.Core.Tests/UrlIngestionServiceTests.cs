using Microsoft.Extensions.Logging.Abstractions;
using StatusMonitor.Core.Models;
using StatusMonitor.Core.Services;
using StatusMonitor.Core.Storage;
using StatusMonitor.Core.Tenancy;
using Xunit;

namespace StatusMonitor.Core.Tests;

public class UrlIngestionServiceTests
{
    private sealed class FakeUrlRepository : IMonitoredUrlRepository
    {
        public List<MonitoredUrlEntity> Urls { get; } = [];

        public Task<IReadOnlyList<MonitoredUrlEntity>> ListAsync(string tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MonitoredUrlEntity>>(
                Urls.Where(u => u.PartitionKey == tenantId).ToList());

        public Task<int> CountAsync(string tenantId, CancellationToken ct = default) =>
            Task.FromResult(Urls.Count(u => u.PartitionKey == tenantId));

        public Task UpsertAsync(MonitoredUrlEntity url, CancellationToken ct = default)
        {
            Urls.Add(url);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string tenantId, string urlKey, CancellationToken ct = default)
        {
            Urls.RemoveAll(u => u.PartitionKey == tenantId && u.RowKey == urlKey);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeQueuePublisher : IQueuePublisher
    {
        public List<(string Queue, object Message)> Published { get; } = [];

        public Task PublishAsync<T>(string queueName, T message, CancellationToken ct = default)
        {
            Published.Add((queueName, message!));
            return Task.CompletedTask;
        }
    }

    private static TenantEntity FreeTenant() => new()
    {
        RowKey = "org_123",
        Name = "Acme",
        PlanId = PlanCatalog.FreePlanId,
    };

    [Fact]
    public async Task Accepts_uploads_within_the_free_tier_limit()
    {
        var queue = new FakeQueuePublisher();
        var service = new UrlIngestionService(new FakeUrlRepository(), queue, NullLogger<UrlIngestionService>.Instance);

        var result = await service.IngestRawAsync(FreeTenant(), "Site A,https://a.example.com\nSite B,https://b.example.com");

        Assert.True(result.Accepted);
        Assert.Equal(2, result.QueuedCount);
        var (queueName, message) = Assert.Single(queue.Published);
        Assert.Equal(QueueNames.UrlImports, queueName);
        var import = Assert.IsType<UrlImportMessage>(message);
        Assert.Equal("org_123", import.TenantId);
        Assert.All(import.Items, i => Assert.Equal(UrlImportActions.Upsert, i.Action));
    }

    [Fact]
    public async Task Rejects_uploads_that_exceed_the_plan_limit()
    {
        var queue = new FakeQueuePublisher();
        var service = new UrlIngestionService(new FakeUrlRepository(), queue, NullLogger<UrlIngestionService>.Instance);

        var tooMany = string.Join('\n', Enumerable.Range(0, PlanCatalog.Free.MaxUrls + 1)
            .Select(i => $"Site {i},https://site{i}.example.com"));

        var result = await service.IngestRawAsync(FreeTenant(), tooMany);

        Assert.False(result.Accepted);
        Assert.Empty(queue.Published);
        Assert.Contains(result.Errors, e => e.Contains("upgrade", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Reimporting_existing_urls_does_not_count_against_the_limit()
    {
        var repo = new FakeUrlRepository();
        foreach (var i in Enumerable.Range(0, PlanCatalog.Free.MaxUrls))
        {
            repo.Urls.Add(new MonitoredUrlEntity
            {
                PartitionKey = "org_123",
                RowKey = UrlKey.FromName($"Site {i}"),
                UrlName = $"Site {i}",
                Url = $"https://site{i}.example.com",
            });
        }

        var queue = new FakeQueuePublisher();
        var service = new UrlIngestionService(repo, queue, NullLogger<UrlIngestionService>.Instance);

        var result = await service.IngestRawAsync(FreeTenant(), "Site 0,https://site0-updated.example.com");

        Assert.True(result.Accepted);
        Assert.Single(queue.Published);
    }

    [Fact]
    public async Task Rejects_submissions_with_no_valid_urls()
    {
        var queue = new FakeQueuePublisher();
        var service = new UrlIngestionService(new FakeUrlRepository(), queue, NullLogger<UrlIngestionService>.Instance);

        var result = await service.IngestRawAsync(FreeTenant(), "not a url\nalso not a url");

        Assert.False(result.Accepted);
        Assert.Empty(queue.Published);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task Delete_enqueues_a_delete_action()
    {
        var queue = new FakeQueuePublisher();
        var service = new UrlIngestionService(new FakeUrlRepository(), queue, NullLogger<UrlIngestionService>.Instance);

        await service.DeleteAsync(FreeTenant(), "site-a");

        var (_, message) = Assert.Single(queue.Published);
        var import = Assert.IsType<UrlImportMessage>(message);
        var item = Assert.Single(import.Items);
        Assert.Equal(UrlImportActions.Delete, item.Action);
        Assert.Equal("site-a", item.UrlKey);
    }
}
