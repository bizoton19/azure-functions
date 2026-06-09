namespace StatusMonitor.Functions.Auth;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>"Jwt" (default) validates Clerk/WorkOS bearer tokens; "Development" trusts an x-tenant-id header.</summary>
    public string Mode { get; set; } = "Jwt";

    /// <summary>
    /// OIDC issuer. For Clerk this is the instance Frontend API URL
    /// (e.g. https://your-app.clerk.accounts.dev); for WorkOS AuthKit it is
    /// https://api.workos.com/user_management/&lt;client_id&gt;.
    /// </summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>Optional expected audience; leave empty to skip audience validation.</summary>
    public string? Audience { get; set; }

    /// <summary>
    /// Claim that carries the company/organization id. Both Clerk and WorkOS
    /// AuthKit emit "org_id" for organization-scoped sessions.
    /// </summary>
    public string TenantClaim { get; set; } = "org_id";

    public bool IsDevelopmentMode =>
        string.Equals(Mode, "Development", StringComparison.OrdinalIgnoreCase);
}
