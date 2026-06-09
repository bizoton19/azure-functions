using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using StatusMonitor.Core.Tenancy;

namespace StatusMonitor.Functions.Auth;

public sealed record AuthResult(TenantContext? Tenant, string? Error)
{
    public static AuthResult Success(TenantContext tenant) => new(tenant, null);
    public static AuthResult Fail(string error) => new(null, error);
}

/// <summary>
/// Validates bearer tokens issued by the identity provider (Clerk or WorkOS
/// AuthKit — both are standard OIDC issuers) and resolves the calling tenant
/// from the organization claim. Signing keys are discovered from the issuer's
/// JWKS endpoint and cached by <see cref="ConfigurationManager{T}"/>.
/// </summary>
public sealed class TenantAuthenticator
{
    private readonly AuthOptions _options;
    private readonly ILogger<TenantAuthenticator> _logger;
    private readonly JsonWebTokenHandler _tokenHandler = new();
    private readonly IConfigurationManager<OpenIdConnectConfiguration>? _oidcConfigManager;

    public TenantAuthenticator(IOptions<AuthOptions> options, ILogger<TenantAuthenticator> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (!_options.IsDevelopmentMode)
        {
            if (string.IsNullOrWhiteSpace(_options.Authority))
            {
                throw new InvalidOperationException(
                    "Auth__Authority must be configured when Auth__Mode is 'Jwt'.");
            }

            var metadataAddress = _options.Authority.TrimEnd('/') + "/.well-known/openid-configuration";
            _oidcConfigManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                metadataAddress, new OpenIdConnectConfigurationRetriever());
        }
    }

    public async Task<AuthResult> AuthenticateAsync(
        string? authorizationHeader, string? devTenantHeader, string? devUserHeader, CancellationToken ct)
    {
        if (_options.IsDevelopmentMode)
        {
            return string.IsNullOrWhiteSpace(devTenantHeader)
                ? AuthResult.Fail("Development mode: provide an x-tenant-id header.")
                : AuthResult.Success(new TenantContext(devTenantHeader.Trim(), devUserHeader?.Trim() ?? "dev-user"));
        }

        if (string.IsNullOrWhiteSpace(authorizationHeader) ||
            !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthResult.Fail("Missing bearer token.");
        }

        var token = authorizationHeader["Bearer ".Length..].Trim();
        var oidcConfig = await _oidcConfigManager!.GetConfigurationAsync(ct);

        var validationParameters = new TokenValidationParameters
        {
            ValidIssuer = oidcConfig.Issuer,
            IssuerSigningKeys = oidcConfig.SigningKeys,
            ValidateIssuer = true,
            ValidateLifetime = true,
            ValidateAudience = !string.IsNullOrWhiteSpace(_options.Audience),
            ValidAudience = _options.Audience,
        };

        var result = await _tokenHandler.ValidateTokenAsync(token, validationParameters);
        if (!result.IsValid)
        {
            _logger.LogWarning(result.Exception, "Bearer token validation failed");
            return AuthResult.Fail("Invalid or expired token.");
        }

        var claims = result.Claims;
        var userId = claims.TryGetValue("sub", out var sub) ? sub?.ToString() : null;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return AuthResult.Fail("Token has no subject claim.");
        }

        // Organization-scoped sessions carry the org id; personal accounts fall
        // back to a user-scoped tenant so individuals can use the free tier.
        var tenantId = claims.TryGetValue(_options.TenantClaim, out var org) &&
                       !string.IsNullOrWhiteSpace(org?.ToString())
            ? org.ToString()!
            : $"user_{userId}";

        return AuthResult.Success(new TenantContext(tenantId, userId));
    }
}
