using System.Net;
using Microsoft.Extensions.Logging;
using StatusMonitor.Core.Models;

namespace StatusMonitor.Core.Services;

/// <summary>
/// Checks a tenant's URLs over HTTP. Ported from the legacy statusPoller_http
/// csx function, with bounded concurrency instead of a sequential loop and
/// per-tenant scoping of every result.
/// </summary>
public sealed class UrlPollerService(IHttpClientFactory httpClientFactory, ILogger<UrlPollerService> logger)
{
    public const string HttpClientName = "url-poller";
    private const int MaxConcurrentChecks = 10;

    public async Task<IReadOnlyList<UrlCheckResult>> PollAsync(
        string tenantId, IReadOnlyList<MonitoredUrlEntity> urls, CancellationToken ct = default)
    {
        using var throttle = new SemaphoreSlim(MaxConcurrentChecks);

        var tasks = urls.Select(async url =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                return await CheckAsync(tenantId, url, ct);
            }
            finally
            {
                throttle.Release();
            }
        });

        return await Task.WhenAll(tasks);
    }

    private async Task<UrlCheckResult> CheckAsync(string tenantId, MonitoredUrlEntity url, CancellationToken ct)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(url.Url, HttpCompletionOption.ResponseContentRead, ct);
            return await Classify(tenantId, url, response, checkedAt, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result(tenantId, url, KnownStatuses.Unreachable,
                "Web page is not responding, the request timed out.", checkedAt);
        }
        catch (HttpRequestException ex)
        {
            logger.LogInformation(ex, "Tenant {TenantId}: {UrlName} unreachable", tenantId, url.UrlName);
            return Result(tenantId, url, KnownStatuses.Unreachable, ex.Message, checkedAt);
        }
    }

    private static async Task<UrlCheckResult> Classify(
        string tenantId, MonitoredUrlEntity url, HttpResponseMessage response,
        DateTimeOffset checkedAt, CancellationToken ct)
    {
        if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.RequestTimeout)
        {
            return Result(tenantId, url, response.StatusCode.ToString(),
                "Web page is not responding, requests are timing out.", checkedAt);
        }

        if (response.StatusCode == HttpStatusCode.GatewayTimeout)
        {
            return Result(tenantId, url, response.StatusCode.ToString(),
                "Upstream gateway timed out. More info: https://developer.mozilla.org/en-US/docs/Web/HTTP/Status/504",
                checkedAt);
        }

        if (!response.IsSuccessStatusCode)
        {
            return Result(tenantId, url, response.StatusCode.ToString(),
                response.ReasonPhrase ?? "Non-success status code.", checkedAt);
        }

        var content = await response.Content.ReadAsStringAsync(ct);
        var falsePositive = ClassifyFalsePositive(content);
        if (falsePositive is not null)
        {
            return Result(tenantId, url, KnownStatuses.Degraded, falsePositive, checkedAt);
        }

        return Result(tenantId, url, KnownStatuses.Ok, response.ReasonPhrase ?? "OK", checkedAt);
    }

    /// <summary>
    /// Detects pages that return 200 but actually serve an error/maintenance page,
    /// which the legacy poller treated as failures despite the success status code.
    /// </summary>
    public static string? ClassifyFalsePositive(string content) =>
        content.Contains("Under Maintenance", StringComparison.OrdinalIgnoreCase)
            ? "Website is responding but with error pages; please check servers, app pools, and the web server."
            : null;

    private static UrlCheckResult Result(
        string tenantId, MonitoredUrlEntity url, string status, string description, DateTimeOffset checkedAt) =>
        new(tenantId, url.UrlKey, url.UrlName, url.Url, status, description, checkedAt);
}
