namespace EmailAgent.Infrastructure.Plugins;

/// <summary>
/// M2T3 (§5 DEFAULT plugin-loading strategy / M2 SQ-3 / SQ-4): the
/// compile-time trust anchor for the three DEFAULT connector plugins. An entry
/// <c>(plugin_id, version, sha256)</c> that matches a candidate's manifest and
/// its DLL bytes is treated as pre-approved: the loader skips the
/// <c>plugin_approvals</c> lookup (SQ-4 loader rule), and skips the HMAC
/// signature check ONLY on the embedded-manifest build-output path — where the
/// manifest lives inside the anchored bytes themselves. A disk companion
/// manifest sits outside those bytes and is always signature-checked, anchor
/// or not (M2T3 nested review, WARNING 1 — see <see cref="PluginLoader"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the anchored bytes come from (test-visible convention):</b> each
/// entry's <see cref="AssemblyFileName"/> names the connector assembly as it
/// sits in the application's own output directory
/// (<c>AppContext.BaseDirectory</c>) — for the test host that is the
/// EmailAgent.IntegrationTests output, which receives the connector DLLs
/// transitively through its EmailAgent.Api project reference. The M2T3
/// integration test <c>DefaultTrustAnchorTests</c> recomputes SHA-256 over
/// exactly those on-disk DLLs and asserts equality with this list.
/// </para>
/// <para>
/// <b>Why hard-coded:</b> the build target that would regenerate these values
/// is a development convenience and is deliberately NOT wired (M2T3 Risks
/// "Build-time SHA-256 generation"). A third-party plugin can never reach
/// this list: adding or changing an entry is a code change plus a passing
/// test (M2T3 Risks "Default plugin auto-approval"). If a DEFAULT connector's
/// bytes change (edit or different compilation inputs), update the matching
/// hash here or <c>DefaultTrustAnchorTests</c> fails. Determinism note:
/// connector projects pin identical compilation across Debug/Release AND
/// across checkout/build paths (<c>DebugType=none</c> plus the other props in
/// EmailAgent.Connectors.Imap.csproj) — verified by hashing both
/// configurations at two distinct container mount paths — so CI (Debug) and
/// the Release gates see the same bytes.
/// </para>
/// </remarks>
public sealed class DefaultTrustAnchor
{
    /// <summary>
    /// (plugin_id, version, assembly file name without extension, sha256 hex)
    /// for the three DEFAULT plugins per §5 / SQ-4.
    /// </summary>
    public IReadOnlyList<(string PluginId, string Version, string AssemblyFileName, byte[] Sha256)> Entries { get; }
        = new List<(string, string, string, byte[])>
        {
            ("imap", "1.0.0", "EmailAgent.Connectors.Imap", Convert.FromHexString("5f15502ff862d6590eb5f2e01b7d623ba2218dcf85bdb00bc5a62a649f46aaa2")),
            ("openai-compatible", "1.0.0", "EmailAgent.Connectors.OpenAI", Convert.FromHexString("b991670b8e40d8aadcf937873e05106c15ed8e7a8f5a6802333300ade31fa58c")),
            ("telegram-send", "1.0.0", "EmailAgent.Connectors.Telegram", Convert.FromHexString("508192b4897b6dfc98cc7257891c652602c6412fb490f1123606d64ca1fcd3a8")),
        };

    /// <summary>
    /// True when <paramref name="pluginId"/> + <paramref name="version"/> +
    /// <paramref name="sha256"/> match an entry exactly (all three — a
    /// tampered DEFAULT DLL falls through to the normal signature/approval
    /// gates and is refused there).
    /// </summary>
    public bool IsDefault(string pluginId, string version, byte[] sha256) =>
        Entries.Any(e =>
            e.PluginId == pluginId
            && e.Version == version
            && e.Sha256.AsSpan().SequenceEqual(sha256));

    /// <summary>
    /// The entry whose <c>AssemblyFileName + ".dll"</c> equals
    /// <paramref name="fileName"/> case-insensitively — pass the full file
    /// name including the <c>.dll</c> suffix (a bare stem returns
    /// <see langword="null"/>), or <see langword="null"/> when no entry
    /// matches. The loader uses this BEFORE mapping a
    /// companion-less drop-folder candidate: only an anchor-named assembly may
    /// be mapped without a companion manifest at all (everything else in the
    /// drop folder is a sibling dependency, never a refused plugin — nested
    /// review round 2, WARNING).
    /// </summary>
    public (string PluginId, string Version, string AssemblyFileName, byte[] Sha256)? FindByAssemblyFile(string fileName)
    {
        foreach (var entry in Entries)
        {
            if (string.Equals(entry.AssemblyFileName + ".dll", fileName, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }
}
