namespace EmailAgent.Infrastructure.Entities;

/// <summary>
/// SQ-4 <c>plugin_signatures</c> row (M2T3): one row per (plugin_id, version)
/// the loader has verified — the record of what bytes loaded and when they
/// were last seen. Unique per (plugin_id, version); <c>assembly_sha256</c> /
/// <c>signature</c> are the raw bytes (hex/base64 only at the JSON edge).
/// </summary>
public sealed class PluginSignature
{
    /// <summary>Surrogate key (the uniqueness contract is the (plugin_id, version) constraint).</summary>
    public Guid Id { get; set; }

    /// <summary>Manifest <c>plugin_id</c>.</summary>
    public string PluginId { get; set; } = null!;

    /// <summary>Manifest <c>version</c> this row records.</summary>
    public string Version { get; set; } = null!;

    /// <summary>SHA-256 over the assembly bytes that were verified.</summary>
    public byte[] AssemblySha256 { get; set; } = null!;

    /// <summary>HMAC-SHA-256 signature recorded at verification time.</summary>
    public byte[] Signature { get; set; } = null!;

    /// <summary>First time this (plugin_id, version) passed the gates.</summary>
    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>Most recent load of this (plugin_id, version).</summary>
    public DateTimeOffset LastSeenAt { get; set; }
}
