namespace EmailAgent.Infrastructure.Plugins;

/// <summary>
/// M2T3: no usable <c>plugin.json</c> for a candidate — no companion file
/// <c>&lt;assembly-name&gt;.plugin.json</c> and no embedded resource, or a
/// manifest missing the identity fields the gates key on. The loader maps
/// this to the <c>plugin_rejected</c> reason <c>manifest_invalid</c>.
/// </summary>
public sealed class PluginManifestException(string message) : Exception(message);
