using EmailAgent.Contracts.ActionHandlers;
using EmailAgent.Infrastructure.Plugins;

namespace EmailAgent.Infrastructure.Actions;

/// <summary>
/// M4T1: production <see cref="IActionHandlerRegistry"/> backed by the host
/// <see cref="IPluginRegistry"/> (the slot M4T1 adds).
/// </summary>
public sealed class PluginActionHandlerRegistry(IPluginRegistry plugins) : IActionHandlerRegistry
{
    /// <inheritdoc />
    public IActionHandler? GetHandler(string typeId) => plugins.GetActionHandler(typeId);
}
