using System.Collections.Concurrent;
using EmailAgent.Contracts.MessageSources;

namespace EmailAgent.Infrastructure.Plugins;

/// <summary>
/// M2T3 default <see cref="IPluginRegistry"/>: a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed with ordinal
/// comparisons so lookups are lock-free on the request path and swaps during
/// SIGHUP reload are atomic per key.
/// </summary>
public sealed class PluginRegistry : IPluginRegistry
{
    private readonly ConcurrentDictionary<string, IMessageSource> _messageSources =
        new(StringComparer.Ordinal);

    public void RegisterMessageSource(string pluginId, IMessageSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(source);
        _messageSources[pluginId] = source;
    }

    public IMessageSource? GetMessageSource(string pluginId) =>
        pluginId is null ? null : _messageSources.GetValueOrDefault(pluginId);

    public IReadOnlyCollection<string> MessageSourceIds => new List<string>(_messageSources.Keys);

    public void Clear() => _messageSources.Clear();
}
