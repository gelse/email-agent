namespace EmailAgent.Infrastructure.Audit;

/// <summary>
/// Binds the <c>Audit</c> configuration section (env <c>AUDIT__HmacKey</c>,
/// §16 / SQ-2 / Q-O10) — the per-install HMAC key for the audit chain.
/// </summary>
public sealed class AuditOptions
{
    /// <summary>Configuration section name (<c>AUDIT__*</c> env vars land here).</summary>
    public const string SectionName = "Audit";

    /// <summary>
    /// The raw key as configured (≥ 32 bytes in any real deployment, §16).
    /// Never logged.
    /// </summary>
    public string? HmacKey { get; set; }
}