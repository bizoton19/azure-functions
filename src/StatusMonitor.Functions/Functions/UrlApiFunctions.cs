using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using StatusMonitor.Core.Services;
using StatusMonitor.Core.Storage;
using StatusMonitor.Functions.Auth;

namespace StatusMonitor.Functions.Functions;

/// <summary>
/// Tenant-scoped URL management API consumed by the status-web SPA.
/// Replaces the legacy urlPersister + statusUrlListReader functions.
/// </summary>
public sealed class UrlApiFunctions(
    ITenantRepository tenants,
    IMonitoredUrlRepository urls,
    UrlIngestionService ingestion)
{
    private sealed record UrlDto(string UrlName, string Url);

    [Function("ListUrls")]
    public async Task<HttpResponseData> ListUrls(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "urls")] HttpRequestData req,
        FunctionContext context, CancellationToken ct)
    {
        var tenant = TenantAuthenticationMiddleware.GetTenant(context);
        var list = await urls.ListAsync(tenant.TenantId, ct);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(list.Select(u => new
        {
            urlKey = u.UrlKey,
            urlName = u.UrlName,
            url = u.Url,
            isActive = u.IsActive,
            createdAtUtc = u.CreatedAtUtc,
        }), ct);
        return response;
    }

    /// <summary>
    /// Accepts either:
    ///  - application/json: [{ "urlName": "...", "url": "..." }, ...] (SPA form / copy-paste UI), or
    ///  - text/csv or text/plain: spreadsheet export or pasted lines of "name,url" / bare URLs.
    /// Returns 202 because persistence is asynchronous via the import queue.
    /// </summary>
    [Function("ImportUrls")]
    public async Task<HttpResponseData> ImportUrls(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "urls")] HttpRequestData req,
        FunctionContext context, CancellationToken ct)
    {
        var tenantContext = TenantAuthenticationMiddleware.GetTenant(context);
        var tenant = await tenants.GetOrCreateAsync(
            tenantContext.TenantId, tenantContext.TenantId, ownerEmail: string.Empty, ct);

        var body = await new StreamReader(req.Body).ReadToEndAsync(ct);
        var contentType = req.Headers.TryGetValues("Content-Type", out var values)
            ? values.FirstOrDefault() ?? string.Empty
            : string.Empty;

        var result = contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase)
            ? await ingestion.IngestAsync(tenant, ParseJsonBody(body), ct)
            : await ingestion.IngestRawAsync(tenant, body, ct);

        var response = req.CreateResponse(result.Accepted ? HttpStatusCode.Accepted : HttpStatusCode.BadRequest);
        await response.WriteAsJsonAsync(new
        {
            accepted = result.Accepted,
            queuedCount = result.QueuedCount,
            currentCount = result.CurrentCount,
            maxUrls = result.MaxUrls,
            errors = result.Errors,
        }, ct);
        return response;
    }

    [Function("DeleteUrl")]
    public async Task<HttpResponseData> DeleteUrl(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "urls/{urlKey}")] HttpRequestData req,
        string urlKey, FunctionContext context, CancellationToken ct)
    {
        var tenantContext = TenantAuthenticationMiddleware.GetTenant(context);
        var tenant = await tenants.GetOrCreateAsync(
            tenantContext.TenantId, tenantContext.TenantId, ownerEmail: string.Empty, ct);

        await ingestion.DeleteAsync(tenant, urlKey, ct);
        return req.CreateResponse(HttpStatusCode.Accepted);
    }

    private static UrlParseOutcome ParseJsonBody(string body)
    {
        List<UrlDto>? dtos;
        try
        {
            dtos = JsonSerializer.Deserialize<List<UrlDto>>(body, Json.Options);
        }
        catch (JsonException)
        {
            return new UrlParseOutcome([], ["Request body is not a valid JSON array of { urlName, url } objects."]);
        }

        var parsed = new List<ParsedUrl>();
        var errors = new List<string>();
        foreach (var dto in dtos ?? [])
        {
            if (string.IsNullOrWhiteSpace(dto.Url))
            {
                errors.Add($"Entry '{dto.UrlName}' has no url.");
                continue;
            }

            var name = string.IsNullOrWhiteSpace(dto.UrlName) ? UrlKey.NameFromUrl(dto.Url) : dto.UrlName;
            parsed.Add(new ParsedUrl(name, dto.Url));
        }

        return new UrlParseOutcome(parsed, errors);
    }
}
