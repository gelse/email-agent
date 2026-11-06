namespace EmailAgent.Core.Options;

/// <summary>
/// Binds the <c>Auth</c> configuration section (env <c>AUTH__*</c>, §16):
/// JWT signing/claims material and refresh-cookie lifetime. Lives in
/// <c>EmailAgent.Core</c> so Infrastructure (the refresh store's TTL) and Api
/// (the JWT issuer) share one type without a project-reference cycle —
/// M1T4 checklist item 1 (§5 layering: options belong below Api).
/// </summary>
public sealed class AuthOptions
{
    /// <summary>Configuration section name (<c>AUTH__*</c> env vars land here).</summary>
    public const string SectionName = "Auth";

    /// <summary>HS256 signing key, ≥ 32 UTF-8 bytes (env <c>AUTH__SigningKey</c>).</summary>
    public string? SigningKey { get; set; }

    /// <summary>JWT <c>iss</c> (env <c>AUTH__Issuer</c>).</summary>
    public string? Issuer { get; set; }

    /// <summary>JWT <c>aud</c> (env <c>AUTH__Audience</c>).</summary>
    public string? Audience { get; set; }

    /// <summary>Access-token TTL (env <c>AUTH__AccessTokenTtl</c>); default 15 minutes.</summary>
    public TimeSpan AccessTokenTtl { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Refresh-token/cookie TTL (env <c>AUTH__RefreshTokenTtl</c>); default 14 days.</summary>
    public TimeSpan RefreshTokenTtl { get; set; } = TimeSpan.FromDays(14);
}
