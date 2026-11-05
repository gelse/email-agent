using System.Text.Json;
using EmailAgent.Contracts;
using EmailAgent.Contracts.Schema;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Schema;

/// <summary>
/// M2T2 matrix: <see cref="ImapConfigSchema.Build"/> is the single source of
/// truth for the IMAP plugin's ConfigSchema (M2T4 reuses it) and validates the
/// canonical good/bad samples.
/// </summary>
[Trait("Category", "Schema")]
public class ImapConfigSchemaTests
{
    private const string KnownGoodConfig =
        """{"host":"imap.example.com","port":993,"use_ssl":true,"username":"alice","password":"secret","mailbox":"INBOX"}""";

    private const string PortOutOfRange =
        """{"host":"imap.example.com","port":99999,"use_ssl":true,"username":"alice","password":"secret","mailbox":"INBOX"}""";

    [Fact]
    public void ImapConfigSchema_Build_Returns_Valid_Schema()
    {
        var schemaJson = JsonSerializer.Serialize(ImapConfigSchema.Build());

        JsonSchemaIntrospector.IsValidSchemaText(schemaJson).Should().BeTrue();
        schemaJson.Should().Contain(
            """"$schema":"https://json-schema.org/draft/2020-12/schema"""");
        schemaJson.Should().Contain(""""title":"IMAP message source config"""");
        schemaJson.Should().Contain("\"additionalProperties\":false");
    }

    [Fact]
    public void ImapConfigSchema_KnownGoodConfig_Validates()
    {
        using var doc = JsonDocument.Parse(KnownGoodConfig);

        var result = JsonSchemaValidator.Validate(ImapConfigSchema.Build(), doc.RootElement);

        result.Ok.Should().BeTrue();
        result.Error.Should().BeNull();
        result.Issues!.Should().BeNull();
    }

    [Fact]
    public void ImapConfigSchema_KnownBadConfig_Fails()
    {
        using var doc = JsonDocument.Parse(PortOutOfRange);

        var result = JsonSchemaValidator.Validate(ImapConfigSchema.Build(), doc.RootElement);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be("schema validation failed");
        result.Issues!.Should().ContainSingle()
            .Which.Should().Be(new ValidationIssue("/port", "maximum"));
    }

    [Fact]
    public void ImapConfigSchema_Build_Returns_Cached_Instance()
    {
        // JsonSchema.Net registers every schema it builds in the process-wide
        // registry; M2T4's `ConfigSchema => ImapConfigSchema.Build()` reads the
        // property per access, so rebuilding would grow the registry each time
        // (review WARNING).
        ImapConfigSchema.Build().Should().BeSameAs(ImapConfigSchema.Build());
    }
}
