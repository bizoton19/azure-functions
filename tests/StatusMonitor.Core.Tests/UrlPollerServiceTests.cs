using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using StatusMonitor.Core.Models;
using StatusMonitor.Core.Services;
using Xunit;

namespace StatusMonitor.Core.Tests;

public class UrlPollerServiceTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(responder(request));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static UrlPollerService CreatePoller(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new StubHttpClientFactory(new StubHandler(responder)), NullLogger<UrlPollerService>.Instance);

    private static MonitoredUrlEntity Url(string key, string url) => new()
    {
        PartitionKey = "tenant-1",
        RowKey = key,
        UrlName = key,
        Url = url,
    };

    [Fact]
    public async Task Healthy_pages_report_ok()
    {
        var poller = CreatePoller(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>all good</html>"),
        });

        var results = await poller.PollAsync("tenant-1", [Url("site", "https://example.com")]);

        var result = Assert.Single(results);
        Assert.Equal(KnownStatuses.Ok, result.Status);
        Assert.True(result.IsHealthy);
        Assert.Equal("tenant-1", result.TenantId);
    }

    [Fact]
    public async Task Maintenance_pages_are_flagged_despite_200()
    {
        var poller = CreatePoller(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>Site Under Maintenance</html>"),
        });

        var results = await poller.PollAsync("tenant-1", [Url("site", "https://example.com")]);

        var result = Assert.Single(results);
        Assert.Equal(KnownStatuses.Degraded, result.Status);
        Assert.False(result.IsHealthy);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Failure_status_codes_are_unhealthy(HttpStatusCode statusCode)
    {
        var poller = CreatePoller(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(string.Empty),
        });

        var results = await poller.PollAsync("tenant-1", [Url("site", "https://example.com")]);

        Assert.False(Assert.Single(results).IsHealthy);
    }

    [Fact]
    public async Task Connection_failures_report_unreachable_instead_of_throwing()
    {
        var poller = CreatePoller(_ => throw new HttpRequestException("connection refused"));

        var results = await poller.PollAsync("tenant-1", [Url("site", "https://down.example.com")]);

        var result = Assert.Single(results);
        Assert.Equal(KnownStatuses.Unreachable, result.Status);
    }

    [Fact]
    public async Task Polls_every_url_in_the_batch()
    {
        var seen = new List<string>();
        var poller = CreatePoller(request =>
        {
            lock (seen)
            {
                seen.Add(request.RequestUri!.ToString());
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        });

        var urls = Enumerable.Range(0, 25)
            .Select(i => Url($"site-{i}", $"https://example.com/{i}"))
            .ToList();

        var results = await poller.PollAsync("tenant-1", urls);

        Assert.Equal(25, results.Count);
        Assert.Equal(25, seen.Count);
    }
}
