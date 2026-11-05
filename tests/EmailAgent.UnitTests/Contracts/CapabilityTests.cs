using System.Text.Json;
using EmailAgent.Contracts.Plugins;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Contracts;

/// <summary>
/// M2T1 step 4/15: <see cref="Capability"/> flag composition,
/// <see cref="CapabilityExtensions.Decompose(Capability)"/> in declaration
/// order, and the snake_case JSON contract used by `plugin.json`.
/// </summary>
public class CapabilityTests
{
    [Fact]
    public void Capability_Flags_Compose_And_Decompose()
    {
        var composed = Capability.BlobReadById | Capability.HttpEgress;

        composed.Should().Be(Capability.HttpEgress | Capability.BlobReadById);
        composed.Decompose().Should().Equal(
            new[] { Capability.BlobReadById, Capability.HttpEgress });

        (Capability.ProcessedMessagesWrite | Capability.HttpEgress).Decompose().Should().Equal(
            new[] { Capability.ProcessedMessagesWrite, Capability.HttpEgress });

        // All declared bits decompose in declaration order.
        Capability.EncryptWithSystemKey.Decompose().Should().Equal(
            new[] { Capability.EncryptWithSystemKey });
    }

    [Fact]
    public void Capability_Decompose_None_ReturnsEmpty()
    {
        Capability.None.Decompose().Should().BeEmpty();
    }

    [Fact]
    public void Capability_DeclaredMask_Covers_All_Declared_Members()
    {
        // Guards against the const drifting from the enum: adding a member
        // without extending DeclaredMask would make PluginManifest.FromJsonElement
        // reject perfectly valid manifests as "unknown bits".
        var union = Enum.GetValues<Capability>()
            .Aggregate(Capability.None, (acc, bit) => acc | bit);
        CapabilityExtensions.DeclaredMask.Should().Be(union);
        CapabilityExtensions.DeclaredMask.Should().NotBe(Capability.None);
    }

    [Fact]
    public void Capability_RoundTripsSnakeCaseJson()
    {
        // Single values serialize to the canonical snake_case name (SQ-1).
        JsonSerializer.Serialize(Capability.HttpEgress).Should().Be("\"http_egress\"");
        JsonSerializer.Serialize(Capability.None).Should().Be("\"none\"");

        JsonSerializer.Deserialize<Capability>("\"processed_messages_write\"")
            .Should().Be(Capability.ProcessedMessagesWrite);

        // Combined flags round-trip (read path splits on ", " when present).
        var flags = Capability.BlobReadById | Capability.ScratchDir;
        JsonSerializer.Deserialize<Capability>(JsonSerializer.Serialize(flags))
            .Should().Be(flags);

        // Integer values stay readable for forward compatibility.
        JsonSerializer.Deserialize<Capability>("8").Should().Be(Capability.HttpEgress);
    }
}
