using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using StatusMonitor.Core.Storage;
using StatusMonitor.Core.Tenancy;
using StatusMonitor.Functions.Auth;

namespace StatusMonitor.Functions.Functions;

/// <summary>
/// Tenant-scoped read API for the status dashboard. Replaces the legacy
/// statusSiteStateReader and statusHistoryReader functions.
/// </summary>
public sealed class StatusApiFunctions(
    ITenantRepository tenants,
    IMonitoredUrlRepository urls,
    IUrlStatusRepository statuses)
{
    [Function("GetCurrentStatuses")]
    public async Task<HttpResponseData> GetCurrentStatuses(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "status")] HttpRequestData req,
        FunctionContext context, CancellationToken ct)
    {
        var tenant = TenantAuthenticationMiddleware.GetTenant(context);
        var list = await statuses.ListCurrentAsync(tenant.TenantId, ct);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(list.Select(s => new
        {
            urlKey = s.RowKey,
            urlName = s.UrlName,
            url = s.Url,
            status = s.Status,
            description = s.Description,
            checkedAtUtc = s.CheckedAtUtc,
        }), ct);
        return response;
    }

    [Function("GetUrlHistory")]
    public async Task<HttpResponseData> GetUrlHistory(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "status/{urlKey}/history")] HttpRequestData req,
        string urlKey, FunctionContext context, CancellationToken ct)
    {
        var tenant = TenantAuthenticationMiddleware.GetTenant(context);
        var take = int.TryParse(
            System.Web.HttpUtility.ParseQueryString(req.Url.Query)["take"], out var parsed)
            ? Math.Clamp(parsed, 1, 500)
            : 50;

        var history = await statuses.ListHistoryAsync(tenant.TenantId, urlKey, take, ct);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(history.Select(h => new
        {
            urlKey = h.UrlKey,
            urlName = h.UrlName,
            url = h.Url,
            status = h.Status,
            description = h.Description,
            checkedAtUtc = h.CheckedAtUtc,
        }), ct);
        return response;
    }

    /// <summary>
    /// Returns (and on first call provisions) the caller's tenant, plan, and
    /// usage. The SPA calls this right after sign-in, which is what creates the
    /// company record on the free tier without any manual onboarding step.
    /// </summary>
    [Function("GetMe")]
    public async Task<HttpResponseData> GetMe(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "me")] HttpRequestData req,
        FunctionContext context, CancellationToken ct)
    {
        var tenantContext = TenantAuthenticationMiddleware.GetTenant(context);
        var tenant = await tenants.GetOrCreateAsync(
            tenantContext.TenantId, tenantContext.TenantId, ownerEmail: string.Empty, ct);
        var plan = PlanCatalog.Resolve(tenant.PlanId);
        var urlCount = await urls.CountAsync(tenant.TenantId, ct);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            tenantId = tenant.TenantId,
            name = tenant.Name,
            status = tenant.Status,
            plan = new
            {
                id = plan.Id,
                name = plan.Name,
                maxUrls = plan.MaxUrls,
                pollIntervalMinutes = plan.PollIntervalMinutes,
                maxAlertRecipients = plan.MaxAlertRecipients,
                historyRetentionDays = plan.HistoryRetentionDays,
                monthlyPriceUsd = plan.MonthlyPriceUsd,
            },
            usage = new
            {
                urlCount,
                lastPolledAtUtc = tenant.LastPolledAtUtc,
            },
        }, ct);
        return response;
    }
}
