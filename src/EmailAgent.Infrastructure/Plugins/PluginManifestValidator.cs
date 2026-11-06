using EmailAgent.Contracts;
using EmailAgent.Contracts.Plugins;

namespace EmailAgent.Infrastructure.Plugins;

/// <summary>
/// M2T3 (§9 / §14 / M2 SQ-6): validates a parsed <see cref="PluginManifest"/>
/// against the system allow-list when the loader sees it — the only place the
/// capability gate is enforced (Q-D3 / Q-D6: no in-process runtime
/// enforcement in M2).
/// </summary>
public sealed class PluginManifestValidator
{
    /// <summary>
    /// Capabilities a manifest may request. Every declared
    /// <see cref="Capability"/> bit is currently allowed (M2 SQ-6: blobs,
    /// processed messages, HTTP egress, scratch dir, key usage); unknown bits
    /// fail because they are outside this mask.
    /// </summary>
    public static readonly Capability Allowed =
        Capability.BlobReadById
        | Capability.BlobWriteByOwner
        | Capability.ProcessedMessagesWrite
        | Capability.HttpEgress
        | Capability.ScratchDir
        | Capability.EncryptWithUserDek
        | Capability.EncryptWithSystemKey;

    /// <summary>
    /// Fails when the manifest requests a capability outside
    /// <see cref="Allowed"/>, or when <see cref="Capability.HttpEgress"/> is
    /// requested with an empty <c>http_allowlist</c> (the grant without its
    /// §14 scope). Note the allowlist is checked here at LOAD time only — the
    /// user-provided IMAP host is resolved against it at config-validation
    /// time in M2T4/M2T5, never against the static manifest list in M2.
    /// </summary>
    public ValidationResult Validate(PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        Capability requested = Capability.None;
        foreach (var capability in manifest.Capabilities)
        {
            requested |= capability;
        }

        var denied = requested & ~Allowed;
        if (denied != Capability.None)
        {
            // Decompose() only yields declared bits, so an unknown-bit denial
            // would otherwise render as an empty list — always include the
            // raw mask so the plugin_rejected audit row stays diagnosable.
            var names = string.Join(", ", denied.Decompose());
            return ValidationResult.Failure(
                $"plugin '{manifest.PluginId}' requests capabilities outside the system allow-list: "
                + $"{(names.Length > 0 ? names + " " : "")}0x{(long)denied:X} (M2 SQ-6 / Q-D6).");
        }

        if (manifest.Capabilities.Contains(Capability.HttpEgress) && manifest.HttpAllowlist.Count == 0)
        {
            return ValidationResult.Failure(
                $"plugin '{manifest.PluginId}' requests http_egress but its http_allowlist is empty (§14).");
        }

        return ValidationResult.Success();
    }
}
