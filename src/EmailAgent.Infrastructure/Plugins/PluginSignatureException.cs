namespace EmailAgent.Infrastructure.Plugins;

/// <summary>
/// M2T3: a plugin failed the signature gate. <see cref="Reason"/> is the
/// machine-readable value the loader writes into the <c>plugin_rejected</c>
/// audit row's <c>metadata_json</c> (acceptance criterion 2: reason
/// <c>signature_mismatch</c>).
/// </summary>
public sealed class PluginSignatureException(string reason, string message) : Exception(message)
{
    /// <summary>Signature field absent from <c>plugin.json</c>.</summary>
    public const string Unsigned = "unsigned";

    /// <summary>
    /// HMAC-SHA-256 over the DLL bytes and the canonical companion manifest
    /// did not match (tampered bytes or manifest, or wrong key).
    /// </summary>
    public const string SignatureMismatch = "signature_mismatch";

    /// <summary>Which signature rule failed; one of the constants on this type.</summary>
    public string Reason { get; } = reason;
}
