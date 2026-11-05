using System.Text.Json;
using EmailAgent.Contracts.Plugins;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Contracts;

/// <summary>
/// M2T1 step 13/15: <see cref="PluginManifest"/> element parsing (the M2 SQ-1
/// `plugin.json` example), serialization with the canonical field names, and
/// required-field rejection.
/// </summary>
public class PluginManifestTests
{
    private const string ManifestJson =
        """
        {
          "plugin_id": "imap",
          "version": "1.0.0",
          "assembly": "EmailAgent.Connectors.Imap.dll",
          "type": "EmailAgent.Connectors.Imap.ImapMessageSource",
          "capabilities": ["processed_messages_write", "http_egress"],
          "http_allowlist": ["imap.example.com", "imap.gmail.com"],
          "notes": "Default IMAP message source plugin"
        }
        """;

    [Fact]
    public void PluginManifest_FromJson_Reads_AllFields()
    {
        using var doc = JsonDocument.Parse(ManifestJson);

        var manifest = PluginManifest.FromJsonElement(doc.RootElement);

        manifest.PluginId.Should().Be("imap");
        manifest.Version.Should().Be("1.0.0");
        manifest.AssemblyName.Should().Be("EmailAgent.Connectors.Imap.dll");
        manifest.TypeName.Should().Be("EmailAgent.Connectors.Imap.ImapMessageSource");
        manifest.Capabilities.Should().BeEquivalentTo(
            new[] { Capability.ProcessedMessagesWrite, Capability.HttpEgress });
        manifest.HttpAllowlist.Should().Equal("imap.example.com", "imap.gmail.com");
        manifest.Notes.Should().Be("Default IMAP message source plugin");
    }

    [Fact]
    public void PluginManifest_Serializes_WithCanonicalNames_And_RoundTripsThroughFromJson()
    {
        var original = new PluginManifest(
            PluginId: "imap",
            Version: "1.0.0",
            AssemblyName: "EmailAgent.Connectors.Imap.dll",
            TypeName: "EmailAgent.Connectors.Imap.ImapMessageSource",
            Capabilities: new HashSet<Capability>
            {
                Capability.ProcessedMessagesWrite,
                Capability.HttpEgress,
            },
            HttpAllowlist: ["imap.example.com"],
            Notes: "note");

        var json = JsonSerializer.Serialize(original);

        json.Should().Contain("\"plugin_id\":\"imap\"");
        json.Should().Contain("\"assembly\":\"EmailAgent.Connectors.Imap.dll\"");
        json.Should().Contain("\"type\":\"EmailAgent.Connectors.Imap.ImapMessageSource\"");
        json.Should().Contain("\"processed_messages_write\"");
        json.Should().Contain("\"http_egress\"");
        json.Should().Contain("\"http_allowlist\":[\"imap.example.com\"]");
        json.Should().Contain("\"notes\":\"note\"");

        using var doc = JsonDocument.Parse(json);
        var back = PluginManifest.FromJsonElement(doc.RootElement);

        back.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void PluginManifest_FromJson_DefaultsOptionalMembersWhenAbsent()
    {
        using var doc = JsonDocument.Parse(
            """{"plugin_id":"x","version":"1.0.0","assembly":"a.dll","type":"T"}""");

        var manifest = PluginManifest.FromJsonElement(doc.RootElement);

        manifest.Capabilities.Should().BeEmpty();
        manifest.HttpAllowlist.Should().BeEmpty();
        manifest.Notes.Should().BeNull();
    }

    [Fact]
    public void PluginManifest_FromJson_NormalizesCompositeCapabilities_And_RejectsUnknownOnes()
    {
        // A combined comma-separated value (what the enum converter itself
        // emits for a composite) decomposes into individual bits, so
        // set-membership grant checks see the two intended capabilities.
        using (var combined = JsonDocument.Parse(
            """{"plugin_id":"x","version":"1.0.0","assembly":"a.dll","type":"T","capabilities":["processed_messages_write, http_egress"]}"""))
        {
            var manifest = PluginManifest.FromJsonElement(combined.RootElement);
            manifest.Capabilities.Should().BeEquivalentTo(
                new[] { Capability.ProcessedMessagesWrite, Capability.HttpEgress });
            manifest.Capabilities.Contains(Capability.ProcessedMessagesWrite).Should().BeTrue();
            manifest.Capabilities.Contains(Capability.HttpEgress).Should().BeTrue();
        }

        // Unknown capability name -> rejected by the enum converter.
        using (var unknownName = JsonDocument.Parse(
            """{"plugin_id":"x","version":"1.0.0","assembly":"a.dll","type":"T","capabilities":["banana"]}"""))
        {
            var act = () => PluginManifest.FromJsonElement(unknownName.RootElement);
            act.Should().Throw<JsonException>();
        }

        // Unknown numeric bit -> rejected by the declared-bits mask, never
        // silently dropped.
        using (var unknownBits = JsonDocument.Parse(
            """{"plugin_id":"x","version":"1.0.0","assembly":"a.dll","type":"T","capabilities":[128]}"""))
        {
            var act = () => PluginManifest.FromJsonElement(unknownBits.RootElement);
            act.Should().Throw<JsonException>().WithMessage("*unknown bits*");
        }
    }

    [Fact]
    public void PluginManifest_FromJsonFile_ReadsManifestFromDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plugin-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, ManifestJson);

            var manifest = PluginManifest.FromJsonFile(path);

            manifest.PluginId.Should().Be("imap");
            manifest.Capabilities.Should().BeEquivalentTo(
                new[] { Capability.ProcessedMessagesWrite, Capability.HttpEgress });
            manifest.HttpAllowlist.Should().Equal("imap.example.com", "imap.gmail.com");
        }
        finally
        {
            File.Delete(path); // no-op when the write never happened
        }
    }

    [Fact]
    public void PluginManifest_FromJson_MissingRequiredOrMistypedFields_Throws()
    {
        // Missing required "type".
        using (var missingType = JsonDocument.Parse(
            """{"plugin_id":"x","version":"1.0.0","assembly":"a.dll"}"""))
        {
            var act = () => PluginManifest.FromJsonElement(missingType.RootElement);
            act.Should().Throw<JsonException>().WithMessage("*type*");
        }

        // Not an object.
        using (var notObject = JsonDocument.Parse("[1]"))
        {
            var act = () => PluginManifest.FromJsonElement(notObject.RootElement);
            act.Should().Throw<JsonException>();
        }

        // Mistyped capabilities member.
        using (var badCapabilities = JsonDocument.Parse(
            """{"plugin_id":"x","version":"1.0.0","assembly":"a.dll","type":"T","capabilities":"nope"}"""))
        {
            var act = () => PluginManifest.FromJsonElement(badCapabilities.RootElement);
            act.Should().Throw<JsonException>();
        }
    }
}
