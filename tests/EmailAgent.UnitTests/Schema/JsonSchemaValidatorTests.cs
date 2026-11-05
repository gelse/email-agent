using System.Text.Json;
using EmailAgent.Contracts;
using EmailAgent.Contracts.Schema;
using FluentAssertions;
using Json.Schema;
using Xunit;

namespace EmailAgent.UnitTests.Schema;

/// <summary>
/// M2T2 matrix: <see cref="JsonSchemaValidator"/> against the canonical
/// good/bad samples. Issue paths are JSON Pointers (<c>/password</c>,
/// <c>/tls/min_version</c>) and reasons are the failing keywords, per the
/// task's Risks section.
/// </summary>
[Trait("Category", "Schema")]
public class JsonSchemaValidatorTests
{
    private const string KnownGoodConfig =
        """{"host":"imap.example.com","port":993,"use_ssl":true,"username":"alice","password":"secret","mailbox":"INBOX"}""";

    private const string MissingPassword =
        """{"host":"imap.example.com","port":993,"use_ssl":true,"username":"alice","mailbox":"INBOX"}""";

    private const string PortOutOfRange =
        """{"host":"imap.example.com","port":99999,"use_ssl":true,"username":"alice","password":"secret","mailbox":"INBOX"}""";

    private const string PortBelowMinimum =
        """{"host":"imap.example.com","port":0,"use_ssl":true,"username":"alice","password":"secret","mailbox":"INBOX"}""";

    private const string PortNotANumber =
        """{"host":"imap.example.com","port":"not-a-number","use_ssl":true,"username":"alice","password":"secret","mailbox":"INBOX"}""";

    private const string UnknownProperty =
        """{"host":"imap.example.com","port":993,"use_ssl":true,"username":"alice","password":"secret","mailbox":"INBOX","extra":1}""";

    private const string UseSslNotABoolean =
        """{"host":"imap.example.com","port":993,"use_ssl":"yes","username":"alice","password":"secret","mailbox":"INBOX"}""";

    private const string NestedSchema =
        """{"type":"object","additionalProperties":false,"properties":{"tls":{"type":"object","additionalProperties":false,"required":["min_version"],"properties":{"min_version":{"type":"integer"}}}}}""";

    private const string NestedBadInstance = """{"tls":{"min_version":"1.2"}}""";

    [Fact]
    public void JsonSchemaValidator_ValidConfig_ReturnsOk()
    {
        using var doc = JsonDocument.Parse(KnownGoodConfig);

        var result = JsonSchemaValidator.Validate(ImapConfigSchema.Build(), doc.RootElement);

        result.Ok.Should().BeTrue();
        result.Error.Should().BeNull();
        result.Issues!.Should().BeNull();
    }

    [Fact]
    public void JsonSchemaValidator_MissingRequiredField_ReturnsFailure_WithIssues()
    {
        using var doc = JsonDocument.Parse(MissingPassword);

        var result = JsonSchemaValidator.Validate(ImapConfigSchema.Build(), doc.RootElement);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be("schema validation failed");
        result.Issues!.Should().ContainSingle()
            .Which.Should().Be(new ValidationIssue("/password", "required"));
    }

    [Fact]
    public void JsonSchemaValidator_PortOutOfRange_ReturnsFailure()
    {
        using var doc = JsonDocument.Parse(PortOutOfRange);

        var result = JsonSchemaValidator.Validate(ImapConfigSchema.Build(), doc.RootElement);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be("schema validation failed");
        result.Issues!.Should().ContainSingle()
            .Which.Should().Be(new ValidationIssue("/port", "maximum"));
    }

    [Fact]
    public void JsonSchemaValidator_PortBelowMinimum_ReturnsFailure()
    {
        using var doc = JsonDocument.Parse(PortBelowMinimum);

        var result = JsonSchemaValidator.Validate(ImapConfigSchema.Build(), doc.RootElement);

        result.Ok.Should().BeFalse();
        result.Issues!.Should().ContainSingle()
            .Which.Should().Be(new ValidationIssue("/port", "minimum"));
    }

    [Fact]
    public void JsonSchemaValidator_UnknownProperty_ReturnsFailure_If_AdditionalPropertiesFalse()
    {
        using var doc = JsonDocument.Parse(UnknownProperty);

        var result = JsonSchemaValidator.Validate(ImapConfigSchema.Build(), doc.RootElement);

        result.Ok.Should().BeFalse();
        result.Issues!.Should().ContainSingle()
            .Which.Should().Be(new ValidationIssue("/extra", "additionalProperties"));
    }

    [Fact]
    public void JsonSchemaValidator_WrongType_ReturnsFailure_WithPath()
    {
        using var badPort = JsonDocument.Parse(PortNotANumber);
        using var badUseSsl = JsonDocument.Parse(UseSslNotABoolean);
        var schema = ImapConfigSchema.Build();

        var portResult = JsonSchemaValidator.Validate(schema, badPort.RootElement);
        var useSslResult = JsonSchemaValidator.Validate(schema, badUseSsl.RootElement);

        portResult.Ok.Should().BeFalse();
        portResult.Issues!.Should().ContainSingle()
            .Which.Should().Be(new ValidationIssue("/port", "type"));

        useSslResult.Ok.Should().BeFalse();
        useSslResult.Issues!.Should().ContainSingle()
            .Which.Should().Be(new ValidationIssue("/use_ssl", "type"));
    }

    [Fact]
    public void JsonSchemaValidator_NestedObject_Reports_Path()
    {
        var schema = JsonSchema.FromText(NestedSchema);
        using var doc = JsonDocument.Parse(NestedBadInstance);

        var result = JsonSchemaValidator.Validate(schema, doc.RootElement);

        result.Ok.Should().BeFalse();
        result.Issues!.Should().ContainSingle()
            .Which.Should().Be(new ValidationIssue("/tls/min_version", "type"));
    }

    [Fact]
    public void JsonSchemaValidator_Defaults_AreApplied_When_Instance_Omits_Them()
    {
        // Task-file note (M2T2 `## Progress`): the plan's example omits `port`
        // from the IMAP config, but `port` is in `required`, so that instance
        // correctly fails. The matrix intent is that a defaulted member the
        // schema does not require may be omitted, and that the default remains
        // available to the consumer — hence the two assertions below.
        const string schemaWithDefault =
            """{"type":"object","additionalProperties":false,"properties":{"port":{"type":"integer","default":993}}}""";

        var result = JsonSchemaValidator.Validate(schemaWithDefault, "{}");

        result.Ok.Should().BeTrue();
        result.Error.Should().BeNull();
        result.Issues!.Should().BeNull();

        var imapJson = JsonSerializer.Serialize(ImapConfigSchema.Build());
        imapJson.Should().Contain("\"default\":993")
            .And.Contain("\"default\":true")
            .And.Contain("\"default\":\"INBOX\"");
    }

    [Fact]
    public void JsonSchemaValidator_StringOverloads_ParseBothDocuments()
    {
        var schemaJson = JsonSerializer.Serialize(ImapConfigSchema.Build());
        using var goodDoc = JsonDocument.Parse(KnownGoodConfig);
        using var missingPasswordDoc = JsonDocument.Parse(MissingPassword);

        var fromSchemaAndElement = JsonSchemaValidator.Validate(
            ImapConfigSchema.Build(), goodDoc.RootElement);
        var fromSchemaTextAndElement = JsonSchemaValidator.Validate(
            schemaJson, missingPasswordDoc.RootElement);
        var fromTwoStrings = JsonSchemaValidator.Validate(schemaJson, PortOutOfRange);

        fromSchemaAndElement.Ok.Should().BeTrue();

        fromSchemaTextAndElement.Ok.Should().BeFalse();
        fromSchemaTextAndElement.Issues!.Should().ContainSingle()
            .Which.Should().Be(new ValidationIssue("/password", "required"));

        fromTwoStrings.Ok.Should().BeFalse();
        fromTwoStrings.Error.Should().Be("schema validation failed");
        fromTwoStrings.Issues!.Should().ContainSingle()
            .Which.Should().Be(new ValidationIssue("/port", "maximum"));
    }

    [Fact]
    public void JsonSchemaValidator_MalformedInput_Reports_WhichDocument_Is_Broken()
    {
        var schemaJson = JsonSerializer.Serialize(ImapConfigSchema.Build());

        var badSchema = JsonSchemaValidator.Validate("{ not json", KnownGoodConfig);
        var badInstance = JsonSchemaValidator.Validate(schemaJson, "not json");

        badSchema.Ok.Should().BeFalse();
        badSchema.Error.Should().Be("invalid schema JSON");

        badInstance.Ok.Should().BeFalse();
        badInstance.Error.Should().Be("invalid instance JSON");
    }

    [Fact]
    public void JsonSchemaValidator_UnresolvableRef_ReturnsFailure_InsteadOfThrowing()
    {
        // JsonSchema.Net resolves $ref lazily at evaluation: this text parses
        // fine and only fails when an instance reaches the reference. Validate
        // must map that to a failing result (review WARNING — M2T5 would
        // otherwise surface an unhandled exception as a 500).
        const string danglingRefSchema =
            """{"type":"object","properties":{"x":{"$ref":"#/$defs/missing"}}}""";
        using var doc = JsonDocument.Parse("""{"x":1}""");

        var result = JsonSchemaValidator.Validate(danglingRefSchema, doc.RootElement);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be("schema evaluation failed");
    }

    [Fact]
    public void JsonSchemaValidator_SameIdSchemaText_IsValidatedTwiceInOneProcess()
    {
        // JsonSchema.Net's process-wide registry refuses to register one $id
        // twice; each call must parse into its own registry so a valid plugin
        // schema keeps validating on save AND on fetch-now (review WARNING).
        const string schemaWithId =
            """{"$id":"https://example.com/m2t2-dup-validate","type":"object","properties":{"a":{"type":"string"}}}""";
        using var doc = JsonDocument.Parse("""{"a":"x"}""");

        var first = JsonSchemaValidator.Validate(schemaWithId, doc.RootElement);
        var second = JsonSchemaValidator.Validate(schemaWithId, doc.RootElement);

        first.Ok.Should().BeTrue();
        second.Ok.Should().BeTrue();
    }

    [Fact]
    public void JsonSchemaValidator_FalseSchema_Reports_RootIssue()
    {
        var result = JsonSchemaValidator.Validate("false", "123");

        result.Ok.Should().BeFalse();
        result.Error.Should().Be("schema validation failed");
        result.Issues!.Should().ContainSingle()
            .Which.Should().Be(new ValidationIssue("", "false"));
    }

    [Fact]
    public void JsonSchemaValidator_MissingMultipleRequired_FilesOneIssuePerMember()
    {
        const string schemaJson = """{"type":"object","required":["password","port"]}""";

        var result = JsonSchemaValidator.Validate(schemaJson, "{}");

        result.Ok.Should().BeFalse();
        result.Issues!.Should().HaveCount(2)
            .And.Contain(new ValidationIssue("/password", "required"))
            .And.Contain(new ValidationIssue("/port", "required"));
    }

    [Fact]
    public void JsonSchemaValidator_SpecialMemberNames_AreEscapedAndDecoded()
    {
        // `/` must be pointer-escaped (~1); a quote in the name arrives
        // JSON-escaped from JsonSchema.Net ("…) and must be decoded back.
        const string slashNameSchema = """{"type":"object","required":["a/b"]}""";
        const string quoteNameSchema = """{"type":"object","required":["a\"b"]}""";

        var slashResult = JsonSchemaValidator.Validate(slashNameSchema, "{}");
        var quoteResult = JsonSchemaValidator.Validate(quoteNameSchema, "{}");

        slashResult.Issues!.Should().ContainSingle()
            .Which.Should().Be(new ValidationIssue("/a~1b", "required"));
        quoteResult.Issues!.Should().ContainSingle()
            .Which.Should().Be(new ValidationIssue("/a\"b", "required"));
    }
}
