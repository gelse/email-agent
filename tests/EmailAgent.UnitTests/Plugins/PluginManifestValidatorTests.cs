using EmailAgent.Contracts.Plugins;
using EmailAgent.Infrastructure.Plugins;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Plugins;

/// <summary>
/// M2T3 (§9 / §14 / M2 SQ-6): the capability gate the loader applies to every
/// manifest it parses — the only enforcement point in M2 (Q-D3 / Q-D6).
/// </summary>
public class PluginManifestValidatorTests
{
    private static PluginManifest Manifest(
        IReadOnlySet<Capability> capabilities, IReadOnlyList<string> allowlist) =>
        new(
            PluginId: "unit-test-plugin",
            Version: "9.9.9",
            AssemblyName: "UnitTest.Plugin.dll",
            TypeName: "UnitTest.Plugin.PluginType",
            Capabilities: capabilities,
            HttpAllowlist: allowlist);

    [Fact]
    public void Manifest_With_Allowed_Capabilities_Passes()
    {
        var manifest = Manifest(
            new HashSet<Capability>(PluginManifestValidator.Allowed.Decompose()),
            allowlist: ["imap.example.com"]);

        var result = new PluginManifestValidator().Validate(manifest);

        result.Ok.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    [Fact]
    public void Manifest_With_Disallowed_Capability_Fails()
    {
        // A bit outside the declared enum (and therefore outside the system
        // allow-list) — constructable only in-process, which is exactly the
        // shape a hand-edited or future-versioned manifest could carry.
        const Capability hypothetical = (Capability)0x80;
        var manifest = Manifest(new HashSet<Capability> { hypothetical }, allowlist: []);

        var result = new PluginManifestValidator().Validate(manifest);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("outside the system allow-list");
    }

    [Fact]
    public void Manifest_With_Empty_HttpAllowlist_Fails()
    {
        var manifest = Manifest(
            new HashSet<Capability> { Capability.HttpEgress },
            allowlist: []);

        var result = new PluginManifestValidator().Validate(manifest);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("http_allowlist is empty");
    }
}
