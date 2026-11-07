using System.Text.Json;
using EmailAgent.Contracts;
using EmailAgent.Contracts.Schema;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Connectors.OpenAI;

/// <summary>
/// M3T1 matrix for <see cref="OpenAICompatibleConfigSchema"/> (task step 4,
/// the M2T2 <c>ImapConfigSchema</c> pattern): three required fields, strict
/// <c>additionalProperties</c>, the http(s) pattern on <c>base_url</c>, and the
/// cached single instance every consumer reuses.
/// </summary>
[Trait("Category", "M3T1")]
public class OpenAICompatibleConfigSchemaTests
{
    private const string KnownGoodConfig =
        """{"base_url":"https://llm.example.test/v1","api_key":"sk-test-redacted","model_name":"gpt-4o-mini"}""";

    private const string UnknownMember =
        """{"base_url":"https://llm.example.test/v1","api_key":"sk-test-redacted","model_name":"gpt-4o-mini","timeout":30}""";

    private const string FtpBaseUrl =
        """{"base_url":"ftp://llm.example.test/v1","api_key":"sk-test-redacted","model_name":"gpt-4o-mini"}""";

    [Fact]
    public void OpenAICompatibleConfigSchema_Build_ReturnsValidSchema()
    {
        var fields = JsonSchemaIntrospector.ListFields(OpenAICompatibleConfigSchema.Build());

        fields.Should().HaveCount(3, "the engine config is exactly base_url, api_key and model_name");
        fields.Select(f => f.Name).Should().BeEquivalentTo(new[] { "base_url", "api_key", "model_name" });
        fields.Should().OnlyContain(f => f.Required, "every engine-config member is mandatory");
    }

    [Fact]
    public void OpenAICompatibleConfigSchema_Build_ReturnsCachedInstance()
    {
        // JsonSchema.Net registers every schema it builds process-wide; the
        // engine reads ConfigSchema per call (ImapConfigSchema review finding).
        OpenAICompatibleConfigSchema.Build().Should().BeSameAs(OpenAICompatibleConfigSchema.Build());
    }

    [Fact]
    public void OpenAICompatibleConfigSchema_KnownGoodConfig_Validates()
    {
        using var doc = JsonDocument.Parse(KnownGoodConfig);

        var result = JsonSchemaValidator.Validate(OpenAICompatibleConfigSchema.Build(), doc.RootElement);

        result.Ok.Should().BeTrue();
        result.Issues.Should().BeNull();
    }

    [Fact]
    public void OpenAICompatibleConfigSchema_UnknownMember_Fails()
    {
        using var doc = JsonDocument.Parse(UnknownMember);

        var result = JsonSchemaValidator.Validate(OpenAICompatibleConfigSchema.Build(), doc.RootElement);

        result.Ok.Should().BeFalse();
        result.Issues!.Should().Contain(i => i.Path.EndsWith("timeout", StringComparison.Ordinal));
    }

    [Fact]
    public void OpenAICompatibleConfigSchema_FtpBaseUrl_FailsPattern()
    {
        using var doc = JsonDocument.Parse(FtpBaseUrl);

        var result = JsonSchemaValidator.Validate(OpenAICompatibleConfigSchema.Build(), doc.RootElement);

        result.Ok.Should().BeFalse();
        result.Issues!.Should().Contain(new ValidationIssue("/base_url", "pattern"));
    }
}
