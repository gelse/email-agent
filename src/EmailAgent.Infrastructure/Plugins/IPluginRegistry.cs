using EmailAgent.Contracts.AgentEngines;
using EmailAgent.Contracts.MessageSources;

namespace EmailAgent.Infrastructure.Plugins;

/// <summary>
/// M2T3: in-process registry of successfully loaded plugins, keyed by the
/// manifest's <c>plugin_id</c> — for every shipped plugin that value equals
/// the plugin's own type id (<c>imap</c>, <c>openai-compatible</c>,
/// <c>telegram-send</c>), which is what <c>message_sources.type_id</c> stores
/// and what the M2T5 endpoint layer looks up (<c>POST /v1/user/sources</c> →
/// <c>GetMessageSource(typeId)</c>). Thread-safe: the loader writes from the
/// host-startup / SIGHUP-reload paths while request threads read
/// concurrently (§9).
/// </summary>
/// <remarks>
/// <para>
/// Each plugin occupies exactly ONE slot: the slot its declared entry type
/// implements — <see cref="IMessageSource"/> or <see cref="IAgentEngine"/>.
/// The <see cref="IAgentEngine"/> slot was added with its implementation in
/// M3T1 (the M2 wording "added together with their implementations (M3/M8)"
/// made it pending); <c>IActionHandler</c> still waits for M8.
/// </para>
/// <para>
/// Re-registering an existing key overwrites it (later candidate wins: a
/// drop-folder plugin overrides the same-id DEFAULT build-output plugin); the
/// entry count per key stays exactly one. Both slots participate in M2T7
/// refusal-wins: the loader unregisters the refused plugin_id from BOTH slots.
/// </para>
/// </remarks>
public interface IPluginRegistry
{
    /// <summary>Registers (or replaces) the message source under <paramref name="pluginId"/>.</summary>
    void RegisterMessageSource(string pluginId, IMessageSource source);

    /// <summary>Returns the message source registered under <paramref name="pluginId"/>, or <c>null</c>.</summary>
    IMessageSource? GetMessageSource(string pluginId);

    /// <summary>
    /// Removes the message-source entry under <paramref name="pluginId"/> if
    /// present (no-op when absent). M2T7 refusal-wins: the loader calls this
    /// after a reload scan for every plugin_id a refused drop-in candidate
    /// claimed, so a same-id build-output (DEFAULT) plugin never survives the
    /// cycle while its drop-in counterpart failed a gate —
    /// <c>test-connection</c> then resolves to <c>plugin-not-registered</c>
    /// instead of a plugin the audit trail says was refused.
    /// </summary>
    void UnregisterMessageSource(string pluginId);

    /// <summary>Registers (or replaces) the agent engine under <paramref name="pluginId"/> (M3T1).</summary>
    void RegisterAgentEngine(string pluginId, IAgentEngine engine);

    /// <summary>Returns the agent engine registered under <paramref name="pluginId"/>, or <c>null</c> (M3T1).</summary>
    IAgentEngine? GetAgentEngine(string pluginId);

    /// <summary>Removes the agent-engine entry under <paramref name="pluginId"/> if present (M3T1; refusal-wins sees both slots).</summary>
    void UnregisterAgentEngine(string pluginId);

    /// <summary>All registered plugin ids (snapshot; unordered).</summary>
    IReadOnlyCollection<string> MessageSourceIds { get; }

    /// <summary>All registered agent-engine plugin ids (snapshot; unordered; M3T1).</summary>
    IReadOnlyCollection<string> AgentEngineIds { get; }

    /// <summary>Removes every entry from both slots (called by the loader before a reload).</summary>
    void Clear();
}
