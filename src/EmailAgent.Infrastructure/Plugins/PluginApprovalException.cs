namespace EmailAgent.Infrastructure.Plugins;

/// <summary>
/// M2T3 (Q-O5): a signed candidate has no <c>plugin_approvals</c> row for
/// (plugin_id, version) and is not covered by <see cref="DefaultTrustAnchor"/>
/// — fail-closed refusal, audited as <c>plugin_rejected</c> with reason
/// <c>not_approved</c>. The admin UI that inserts approval rows lands in M7b.
/// </summary>
public sealed class PluginApprovalException : Exception
{
    /// <summary>The manifest plugin_id that lacked an approval row.</summary>
    public string PluginId { get; }

    /// <summary>The manifest version that lacked an approval row.</summary>
    public string Version { get; }

    public PluginApprovalException(string pluginId, string version)
        : base(
            $"plugin '{pluginId}' {version} has no plugin_approvals row and does not match "
            + "DefaultTrustAnchor (Q-O5; admin approval UI lands in M7b).")
    {
        PluginId = pluginId;
        Version = version;
    }
}
