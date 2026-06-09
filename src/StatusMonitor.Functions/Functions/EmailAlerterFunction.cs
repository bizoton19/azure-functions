using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SendGrid;
using SendGrid.Helpers.Mail;
using StatusMonitor.Core.Models;
using StatusMonitor.Core.Storage;
using StatusMonitor.Core.Tenancy;

namespace StatusMonitor.Functions.Functions;

/// <summary>
/// Replaces the legacy emailAlerter_csharp function. Sends one email per failed
/// polling run, addressed to the tenant's configured recipients (capped by plan)
/// instead of a single global EMAIL_RECIPIENTS list.
/// </summary>
public sealed class EmailAlerterFunction(
    ITenantRepository tenants,
    IConfiguration configuration,
    ILogger<EmailAlerterFunction> logger)
{
    [Function("EmailAlerter")]
    public async Task Run(
        [QueueTrigger(QueueNames.StatusNotifications, Connection = "AzureWebJobsStorage")] string message,
        CancellationToken ct)
    {
        var alert = Json.Deserialize<AlertMessage>(message);
        if (alert.Failures.Count == 0)
        {
            return;
        }

        var apiKey = configuration["SENDGRID_API_KEY"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning("SENDGRID_API_KEY is not configured; skipping alert for tenant {TenantId}", alert.TenantId);
            return;
        }

        var recipients = await ResolveRecipientsAsync(alert.TenantId, ct);
        if (recipients.Count == 0)
        {
            logger.LogWarning("Tenant {TenantId} has no alert recipients configured", alert.TenantId);
            return;
        }

        var from = new EmailAddress(
            configuration["ALERT_FROM_EMAIL"] ?? "alerts@statusmonitor.example",
            "Status Monitor");

        var mail = MailHelper.CreateSingleEmailToMultipleRecipients(
            from,
            recipients.Select(r => new EmailAddress(r)).ToList(),
            subject: $"Status alert: {alert.Failures.Count} resource(s) failing",
            plainTextContent: null,
            htmlContent: BuildBody(alert));

        var response = await new SendGridClient(apiKey).SendEmailAsync(mail, ct);
        logger.LogInformation(
            "Tenant {TenantId}: alert email for {Count} failures sent with status {Status}",
            alert.TenantId, alert.Failures.Count, response.StatusCode);
    }

    private async Task<IReadOnlyList<string>> ResolveRecipientsAsync(string tenantId, CancellationToken ct)
    {
        var tenant = await tenants.GetAsync(tenantId, ct);
        var configured = tenant?.AlertEmails ?? configuration["EMAIL_RECIPIENTS"] ?? string.Empty;
        var plan = PlanCatalog.Resolve(tenant?.PlanId);

        return configured
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(plan.MaxAlertRecipients)
            .ToList();
    }

    private static string BuildBody(AlertMessage alert)
    {
        var text = new StringBuilder();
        text.AppendLine("<h3>The following resources had or have a status change:</h3>");
        foreach (var state in alert.Failures)
        {
            text.AppendLine($"<p>Name: <strong>{state.UrlName}</strong></p>");
            text.AppendLine($"<p>Url: {state.Url}</p>");
            text.AppendLine($"<p>Poll Status: {state.Status}</p>");
            text.AppendLine($"<p>Status Description: {state.Description}</p>");
            text.AppendLine($"<p>Checked At (UTC): {state.CheckedAtUtc:u}</p>");
            text.AppendLine("<hr/>");
        }

        return text.ToString();
    }
}
