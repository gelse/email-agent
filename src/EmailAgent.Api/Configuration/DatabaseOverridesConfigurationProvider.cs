using Microsoft.Extensions.Configuration;

namespace EmailAgent.Api.Configuration;

/// <summary>
/// The §16 layered-configuration chain ends in the DB-override layer:
/// <c>appsettings.json → appsettings.{Environment}.json → environment
/// variables → DB overrides</c>. M1 has no DB-backed configuration overrides,
/// so M1T1 ships this layer as a registered no-op stub: <see cref="DatabaseOverridesConfigurationProvider"/>
/// deliberately loads nothing, and a later milestone replaces it with a
/// provider that reads admin-editable overrides from the database.
/// </summary>
public sealed class DatabaseOverridesConfigurationSource : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new DatabaseOverridesConfigurationProvider();
}

/// <summary>
/// No-op DB-override configuration provider (M1T1 stub for §16).
/// </summary>
public sealed class DatabaseOverridesConfigurationProvider : ConfigurationProvider
{
    /// <inheritdoc />
    public override void Load()
    {
        // Intentionally empty: no DB-backed configuration overrides in M1.
    }
}
