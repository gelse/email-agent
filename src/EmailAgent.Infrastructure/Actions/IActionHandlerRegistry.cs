using EmailAgent.Contracts.ActionHandlers;

namespace EmailAgent.Infrastructure.Actions;

/// <summary>
/// M4T1: narrow seam the dispatcher resolves handlers through (unit tests
/// substitute a fake; production delegates to <see cref="Plugins.IPluginRegistry"/>).
/// </summary>
public interface IActionHandlerRegistry
{
    IActionHandler? GetHandler(string typeId);
}
