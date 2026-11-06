using System.Text.Json;
using EmailAgent.Connectors.OpenAI;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Connectors.OpenAI;

/// <summary>
/// M3T1 unit matrix for <see cref="OpenAICompatibleConfig"/> (task step 3):
/// member mapping through <c>FromJson</c>, the loud failure on a missing /
/// mistyped member and on a non-http(s) <c>base_url</c>, and the credential
/// redaction in <c>ToString</c>.
/// </summary>
[Trait("Category", "M3T1")]
public class OpenAICompatibleConfigTests
{
    private const string ValidConfig =
        """{"base_url":"https://llm.example.test/v1","api_key":"sk-test-redacted","model_name":"gpt-4o-mini"}""";

    private const string MissingBaseUrl =
        """{"api_key":"sk-test-redacted","model_name":"gpt-4o-mini"}""";

    private const string NonHttpBaseUrl =
        """{"base_url":"ftp://llm.example.test/v1","api_key":"sk-test-redacted","model_name":"gpt-4o-mini"}""";

    [Fact]
    public void OpenAICompatibleConfig_FromJson_AppliesRequired()
    {
        using var doc = JsonDocument.Parse(ValidConfig);

        var cfg = OpenAICompatibleConfig.FromJson(doc.RootElement);

        cfg.BaseUrl.Should().Be("https://llm.example.test/v1");
        cfg.ApiKey.Should().Be("sk-test-redacted", "the recorded test key flows through verbatim");
        cfg.ModelName.Should().Be("gpt-4o-mini");
    }

    [Fact]
    public void OpenAICompatibleConfig_FromJson_MissingBaseUrl_ThrowsJsonException()
    {
        using var doc = JsonDocument.Parse(MissingBaseUrl);

        var act = () => OpenAICompatibleConfig.FromJson(doc.RootElement);

        act.Should().Throw<JsonException>()
            .WithMessage("*base_url*", "a missing required member must fail loudly, not default silently");
    }

    [Fact]
    public void OpenAICompatibleConfig_FromJson_NonHttpBaseUrl_ThrowsJsonException()
    {
        using var doc = JsonDocument.Parse(NonHttpBaseUrl);

        var act = () => OpenAICompatibleConfig.FromJson(doc.RootElement);

        act.Should().Throw<JsonException>()
            .WithMessage("*http(s)*", "only absolute http(s) endpoints are callable in v1");
    }

    [Fact]
    public void OpenAICompatibleConfig_ToString_RedactsApiKey()
    {
        using var doc = JsonDocument.Parse(ValidConfig);
        var cfg = OpenAICompatibleConfig.FromJson(doc.RootElement);

        var text = cfg.ToString();

        text.Should().NotContain("sk-test-redacted", "a credential must never reach a log line or exception");
        text.Should().Contain("***").And.Contain("https://llm.example.test/v1");
    }
}
