using EmailAgent.Contracts.MessageSources;

namespace EmailAgent.Infrastructure.Plugins;

/// <summary>
/// M2T3: in-process registry of successfully loaded plugins, keyed by the
/// manifest's <c>plugin_id</c> — for every shipped plugin that value equals
/// the plugin's <see cref="IMessageSource.TypeId"/> (<c>imap</c>,
/// <c>openai-compatible</c>, <c>telegram-send</c>), which is what
/// <c>message_sources.type_id</c> stores and what the M2T5 endpoint layer
/// looks up (<c>POST /v1/user/sources</c> → <c>GetMessageSource(typeId)</c>).
/// Thread-safe: the loader writes from the host-startup / SIGHUP-reload
/// paths while request threads read concurrently (§9).
/// </summary>
/// <remarks>
/// M2 covers <see cref="IMessageSource"/> only — <c>IAgentEngine</c> /
/// <c>IActionHandler</c> slots are added together with their implementations
/// (M3 / M8). Re-registering an existing key overwrites it (later candidate
/// wins: a drop-folder plugin overrides the same-id DEFAULT build-output
/// plugin); the entry count per key stays exactly one.
/// </remarks>
public interface IPluginRegistry
{
    /// <summary>Registers (or replaces) the message source under <paramref name="pluginId"/>.</summary>
    void RegisterMessageSource(string pluginId, IMessageSource source);

    /// <summary>Returns the message source registered under <paramref name="pluginId"/>, or <c>null</c>.</summary>
    IMessageSource? GetMessageSource(string pluginId);

    /// <summary>All registered plugin ids (snapshot; unordered).</summary>
    IReadOnlyCollection<string> MessageSourceIds { get; }

    /// <summary>Removes every entry (called by the loader before a reload).</summary>
    void Clear();
}
