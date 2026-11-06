namespace EmailAgent.Infrastructure.Plugins;

/// <summary>
/// M2T3: the candidate's bytes mapped but no <see cref="Contracts.MessageSources.IMessageSource"/>
/// could be created from it (no implementation, entry type missing, or the
/// type cannot be instantiated). Audited as <c>plugin_rejected</c> with
/// reason <c>load_failed</c>.
/// </summary>
public sealed class PluginLoadException : Exception
{
    public PluginLoadException(string message)
        : base(message)
    {
    }

    public PluginLoadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
