using System.Text.Json;
using EmailAgent.Contracts.Schema;
using FluentAssertions;
using Json.Schema;
using Xunit;

namespace EmailAgent.UnitTests.Schema;

/// <summary>
/// M2T2 matrix: <see cref="JsonSchemaIntrospector"/> — flat field list with
/// required flags (M2T6/M7b), dotted paths for nested keys, and the
/// schema-text gate used by the plugin loader (M2T3).
/// </summary>
[Trait("Category", "Schema")]
public class JsonSchemaIntrospectorTests
{
    private const string NestedSchema =
        """{"type":"object","required":["tls"],"properties":{"tls":{"type":"object","description":"TLS options","required":["min_version"],"properties":{"min_version":{"type":"string","description":"Minimum TLS version"}}}}}""";

    [Fact]
    public void JsonSchemaIntrospector_ListFields_ReturnsAllTopLevelFields_WithRequiredFlag()
    {
        var fields = JsonSchemaIntrospector.ListFields(ImapConfigSchema.Build());

        fields.Should().HaveCount(6);
        fields.Select(f => f.Name).Should().BeEquivalentTo(
            new[] { "host", "port", "use_ssl", "username", "password", "mailbox" });
        fields.Should().OnlyContain(f => f.Required);

        var host = fields.Single(f => f.Name == "host");
        host.Type.Should().Be("string");
        host.Description.Should().Be("IMAP server hostname");

        var port = fields.Single(f => f.Name == "port");
        port.Type.Should().Be("integer");
        port.Description.Should().Be("IMAP port (993 for IMAPS)");

        var useSsl = fields.Single(f => f.Name == "use_ssl");
        useSsl.Type.Should().Be("boolean");
    }

    [Fact]
    public void JsonSchemaIntrospector_ListFields_ForNestedSchema_ReturnsNestedKeysAsDottedPath()
    {
        var fields = JsonSchemaIntrospector.ListFields(JsonSchema.FromText(NestedSchema));

        fields.Should().HaveCount(2);

        var tls = fields.Single(f => f.Name == "tls");
        tls.Type.Should().Be("object");
        tls.Required.Should().BeTrue();
        tls.Description.Should().Be("TLS options");

        var minVersion = fields.Single(f => f.Name == "tls.min_version");
        minVersion.Type.Should().Be("string");
        minVersion.Required.Should().BeTrue();
        minVersion.Description.Should().Be("Minimum TLS version");
    }

    [Fact]
    public void JsonSchemaIntrospector_ListFields_Handles_Union_And_Untyped_Fields()
    {
        const string schemaJson =
            """{"type":"object","required":["alias"],"properties":{"alias":{"type":["string","null"]},"payload":{}}}""";

        var fields = JsonSchemaIntrospector.ListFields(JsonSchema.FromText(schemaJson));

        var alias = fields.Single(f => f.Name == "alias");
        alias.Type.Should().Be("string|null");
        alias.Required.Should().BeTrue();
        alias.Description.Should().BeNull();

        var payload = fields.Single(f => f.Name == "payload");
        payload.Type.Should().Be("any");
        payload.Required.Should().BeFalse();
    }

    [Fact]
    public void JsonSchemaIntrospector_IsValidSchemaText_Accepts_Schemas_And_Rejects_Broken_Text()
    {
        JsonSchemaIntrospector
            .IsValidSchemaText(JsonSerializer.Serialize(ImapConfigSchema.Build()))
            .Should().BeTrue();
        JsonSchemaIntrospector.IsValidSchemaText("{}").Should().BeTrue();
        JsonSchemaIntrospector.IsValidSchemaText("{ not json").Should().BeFalse();
        JsonSchemaIntrospector.IsValidSchemaText("""{"type":5}""").Should().BeFalse();
        // Parses, but cannot be evaluated: a dangling root $ref must not pass
        // the loader gate (review WARNING).
        JsonSchemaIntrospector
            .IsValidSchemaText("""{"$ref":"#/$defs/missing"}""")
            .Should().BeFalse();
    }

    [Fact]
    public void JsonSchemaIntrospector_IsValidSchemaText_SameIdText_Twice_BothReturnTrue()
    {
        // The process-wide schema registry refuses a second registration of
        // one $id; each call parses into its own registry (review WARNING).
        const string schemaWithId =
            """{"$id":"https://example.com/m2t2-dup-intro","type":"object"}""";

        JsonSchemaIntrospector.IsValidSchemaText(schemaWithId).Should().BeTrue();
        JsonSchemaIntrospector.IsValidSchemaText(schemaWithId).Should().BeTrue();
    }

    [Fact]
    public void JsonSchemaIntrospector_NestedRequired_IsRelativeToContainingObject()
    {
        const string schemaJson =
            """{"type":"object","properties":{"tls":{"type":"object","required":["min_version"],"properties":{"min_version":{}}}}}""";

        var fields = JsonSchemaIntrospector.ListFields(JsonSchema.FromText(schemaJson));

        var tls = fields.Single(f => f.Name == "tls");
        tls.Required.Should().BeFalse(); // optional at the top level...

        var minVersion = fields.Single(f => f.Name == "tls.min_version");
        minVersion.Required.Should().BeTrue(); // ...but required within `tls`
    }
}
