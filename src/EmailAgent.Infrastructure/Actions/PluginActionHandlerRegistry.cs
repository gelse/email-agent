using EmailAgent.Contracts.ActionHandlers;
using EmailAgent.Infrastructure.Plugins;

namespace EmailAgent.Infrastructure.Actions;

/// <summary>
/// M4T1: production <see cref="IActionHandlerRegistry"/> backed by the host
/// <see cref="IPluginRegistry"/> (the slot M4T1 adds).
/// </summary>
public sealed class PluginActionHandlerRegistry : IActionHandlerRegistry
{
    private readonly IPluginRegistry _plugins;

    public PluginActionHandlerRegistry(IPluginRegistry plugins) => _plugins = plugins;

    /// <inheritdoc />
    public IActionHandler? GetHandler(string typeId) => _plugins.GetActionHandler(typeId);
}
