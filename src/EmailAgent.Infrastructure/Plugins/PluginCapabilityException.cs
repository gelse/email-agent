namespace EmailAgent.Infrastructure.Plugins;

/// <summary>
/// M2T3 (M2 SQ-6 / Q-D6): the plugin's <c>plugin.json</c> failed
/// <see cref="PluginManifestValidator"/> — the capability gate is enforced at
/// load time only; there is deliberately no in-process runtime enforcement
/// (Q-D3 assembly-level isolation for v1).
/// </summary>
public sealed class PluginCapabilityException(string message, string? pluginId = null) : Exception(message)
{
    /// <summary>
    /// The manifest's declared <c>plugin_id</c>, when known: the candidate has
    /// already passed the signature gate by the time validation runs, so the
    /// capability refusal can (and does) attribute its audit row to it.
    /// </summary>
    public string? PluginId { get; } = pluginId;
}
