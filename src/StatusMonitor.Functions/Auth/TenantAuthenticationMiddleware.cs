using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using StatusMonitor.Core.Tenancy;

namespace StatusMonitor.Functions.Auth;

/// <summary>
/// Runs before every HTTP-triggered function: authenticates the caller and
/// stashes the resolved <see cref="TenantContext"/> on the invocation so
/// functions never see a request without a tenant. Queue and timer triggers
/// pass through untouched (their tenant travels inside the message payload).
/// </summary>
public sealed class TenantAuthenticationMiddleware(TenantAuthenticator authenticator) : IFunctionsWorkerMiddleware
{
    private const string ContextKey = "TenantContext";

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var request = await context.GetHttpRequestDataAsync();
        if (request is null)
        {
            await next(context);
            return;
        }

        var result = await authenticator.AuthenticateAsync(
            GetHeader(request, "Authorization"),
            GetHeader(request, "x-tenant-id"),
            GetHeader(request, "x-user-id"),
            context.CancellationToken);

        if (result.Tenant is null)
        {
            var response = request.CreateResponse(HttpStatusCode.Unauthorized);
            await response.WriteAsJsonAsync(new { error = result.Error });
            context.GetInvocationResult().Value = response;
            return;
        }

        context.Items[ContextKey] = result.Tenant;
        await next(context);
    }

    private static string? GetHeader(HttpRequestData request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    public static TenantContext GetTenant(FunctionContext context) =>
        context.Items.TryGetValue(ContextKey, out var value) && value is TenantContext tenant
            ? tenant
            : throw new InvalidOperationException(
                "No tenant on the invocation; is the function HTTP-triggered and the middleware registered?");
}
