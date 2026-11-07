using System.Collections.Concurrent;
using EmailAgent.Contracts.AgentEngines;
using EmailAgent.Contracts.MessageSources;

namespace EmailAgent.Infrastructure.Plugins;

/// <summary>
/// M2T3 default <see cref="IPluginRegistry"/>: one
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> per slot (message source,
/// agent engine — the second added by M3T1), keyed with ordinal comparisons so
/// lookups are lock-free on the request path and swaps during SIGHUP reload
/// are atomic per key.
/// </summary>
public sealed class PluginRegistry : IPluginRegistry
{
    private readonly ConcurrentDictionary<string, IMessageSource> _messageSources =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, IAgentEngine> _agentEngines =
        new(StringComparer.Ordinal);

    public void RegisterMessageSource(string pluginId, IMessageSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(source);
        _messageSources[pluginId] = source;
    }

    public void UnregisterMessageSource(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        _messageSources.TryRemove(pluginId, out _);
    }

    public IMessageSource? GetMessageSource(string pluginId) =>
        pluginId is null ? null : _messageSources.GetValueOrDefault(pluginId);

    public void RegisterAgentEngine(string pluginId, IAgentEngine engine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(engine);
        _agentEngines[pluginId] = engine;
    }

    public void UnregisterAgentEngine(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        _agentEngines.TryRemove(pluginId, out _);
    }

    public IAgentEngine? GetAgentEngine(string pluginId) =>
        pluginId is null ? null : _agentEngines.GetValueOrDefault(pluginId);

    public IReadOnlyCollection<string> MessageSourceIds => new List<string>(_messageSources.Keys);

    public IReadOnlyCollection<string> AgentEngineIds => new List<string>(_agentEngines.Keys);

    public void Clear()
    {
        _messageSources.Clear();
        _agentEngines.Clear();
    }
}
