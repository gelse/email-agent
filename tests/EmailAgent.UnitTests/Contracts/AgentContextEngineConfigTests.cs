using System.Text.Json;
using EmailAgent.Contracts.AgentEngines;
using EmailAgent.Contracts.MessageSources;
using FluentAssertions;
using Json.Schema;
using Xunit;

namespace EmailAgent.UnitTests.Contracts;

/// <summary>
/// M3T1 additive member <see cref="AgentContext.EngineConfigJson"/> must stay
/// in-process only (SQ-M3-10, §14 hardening note): it carries the decrypted
/// <c>api_key</c>, so a caller binding an <c>AgentRequest</c> body must not be
/// able to inject an endpoint and key into an engine call, and the context must
/// never render the credential verbatim in a log line or an assertion failure.
/// </summary>
[Trait("Category", "M3T1")]
public class AgentContextEngineConfigTests
{
    [Fact]
    public void AgentContext_Serialized_OmitsEngineConfigJsonAndApiKey()
    {
        var context = new AgentContext(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)
        {
            EngineConfigJson = ConfigWithSecretKey(),
        };

        var json = JsonSerializer.Serialize(context);

        json.Should().NotContain("EngineConfigJson", "the engine config is in-process only, never on the wire");
        json.Should().NotContain("api_key", "a request body must not carry a provider credential");
        json.Should().NotContain("sk-test-redacted", "the key marker must never leave the process");
        json.Should().Contain("\"user_id\"", "the M2T1 context wire shape itself is unchanged");
        json.Should().Contain("\"source_id\"");
    }

    [Fact]
    public void AgentContext_RoundTrip_ThroughAgentRequest_OmitsEngineConfigJson()
    {
        var message = new RemoteMessage(
            "1", DateTimeOffset.UnixEpoch, new Dictionary<string, string>(), "body", []);
        var request = new AgentRequest(
            Message: message,
            Prompt: "summarise",
            AllowedActionSchemas: new Dictionary<string, JsonSchema>(),
            Context: new AgentContext(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)
            {
                EngineConfigJson = ConfigWithSecretKey(),
            });

        var json = JsonSerializer.Serialize(request);

        json.Should().NotContain(
            "sk-test-redacted",
            "binding an AgentRequest body must not let a caller inject an endpoint and key (§14)");
        json.Should().NotContain("EngineConfigJson");
    }

    [Fact]
    public void AgentContext_ToString_RedactsEngineConfigJson()
    {
        var withConfig = new AgentContext(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)
        {
            EngineConfigJson = ConfigWithSecretKey(),
        };
        var withoutConfig = new AgentContext(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        var rendered = withConfig.ToString();

        rendered.Should().Contain("EngineConfigJson = ***", "the decrypted api_key must never reach a log line");
        rendered.Should().NotContain("sk-test-redacted");
        rendered.Should().NotContain("api_key");
        rendered.Should().Contain(withConfig.UserId.ToString(), "identity stays visible for diagnosis");

        withoutConfig.ToString().Should().Contain(
            "EngineConfigJson = null", "an absent config reads as null, not as a redaction marker");
    }

    /// <summary>The recorded test key marker only — never a real credential (AGENTS.md #5).</summary>
    private static JsonElement ConfigWithSecretKey()
    {
        using JsonDocument doc = JsonDocument.Parse(
            """
            {"base_url":"https://llm.example.test/v1","api_key":"sk-test-redacted","model_name":"gpt-4o-mini"}
            """);
        return doc.RootElement.Clone();
    }
}
