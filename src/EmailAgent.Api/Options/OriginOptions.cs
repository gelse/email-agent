namespace EmailAgent.Api.Options;

/// <summary>
/// Binds <c>SPA__AllowedOrigins</c> (CSV; default empty = same-origin
/// posture, §13.9 / Q-API3) and <c>API__PublicBaseUrl</c> (canonical public
/// URL; its origin is what the refresh path accepts by default).
/// </summary>
public sealed class OriginOptions
{
    /// <summary>CSV list of allowed browser origins; empty by default.</summary>
    public string AllowedOrigins { get; set; } = string.Empty;

    /// <summary>Canonical public base URL; empty by default.</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;
}