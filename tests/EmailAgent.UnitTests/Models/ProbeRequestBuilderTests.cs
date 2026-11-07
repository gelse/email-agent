using System.Text.Json;
using EmailAgent.Api.Models;
using EmailAgent.Connectors.OpenAI.Http;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Models;

/// <summary>
/// M3T2 — <see cref="ProbeRequestBuilder"/> produces the canonical SQ-M3-3
/// probe shape (unit test named by the task's Scope) and carries the engine
/// configuration in <c>AgentContext.EngineConfigJson</c> only.
/// </summary>
[Trait("Category", "M3T2")]
public class ProbeRequestBuilderTests
{
    private const string BaseUrl = "https://api.example.com/v1";
    private const string ApiKey = "sk-unit-never-in-the-wire-body";
    private const string ModelName = "gpt-4o-mini";

    [Fact]
    public void ProbeRequestBuilder_BuildsCanonicalShape()
    {
        var request = ProbeRequestBuilder.BuildProbeRequest(BaseUrl, ApiKey, ModelName);

        var body = ProbeRequestBuilder.BuildWireBody(request);
        var json = JsonSerializer.Serialize(body, OpenAIJsonOptions.Default);

        // SQ-M3-3 "used verbatim": model + messages + tools + tool_choice +
        // max_tokens + stream, in that order, with the fixed ping tool.
        json.Should().Be(
            "{\"model\":\"gpt-4o-mini\","
            + "\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}],"
            + "\"tools\":[{\"type\":\"function\",\"function\":{"
            + "\"name\":\"ping\",\"description\":\"Probe tool; returns nothing.\","
            + "\"parameters\":{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}}}],"
            + "\"tool_choice\":\"auto\",\"max_tokens\":1,\"stream\":false}");
        json.Should().NotContain(
            ApiKey, "the wire body is serialized into audit_log — it must never carry the credential");
    }

    [Fact]
    public void BuildProbeRequest_CarriesFixedPromptToolAndEngineConfig()
    {
        var request = ProbeRequestBuilder.BuildProbeRequest(BaseUrl, ApiKey, ModelName);

        request.Prompt.Should().Be(
            "ping", "the probe prompt is the fixed SQ-M3-3 string, never user content");
        request.AllowedActionSchemas.Keys.Should().Equal("ping");
        request.Message.Should().BeNull(
            "the probe is engine-only, not message-driven (documented task risk)");
        request.Context.UserId.Should().Be(Guid.Empty);
        request.Context.SourceId.Should().Be(Guid.Empty);

        // The key travels ONLY here — the engine reads it for the Bearer header
        // (SQ-M3-10) and nothing else serializes this document.
        request.Context.EngineConfigJson.Should().NotBeNull();
        var engineConfig = request.Context.EngineConfigJson!.Value;
        engineConfig.ValueKind.Should().Be(JsonValueKind.Object);
        engineConfig.GetProperty("base_url").GetString().Should().Be(BaseUrl);
        engineConfig.GetProperty("api_key").GetString().Should().Be(ApiKey);
        engineConfig.GetProperty("model_name").GetString().Should().Be(ModelName);
    }

    [Fact]
    public void BuildWireBody_BarePingPrompt_YieldsTheSingleCanonicalUserMessage()
    {
        // SQ-M3-9 lenient fallback: the bare "ping" prompt carries no
        // SYSTEM:/USER: markers, so the canonical body has exactly one user
        // message — asserted structurally here as well as in the string-equality
        // test above.
        var request = ProbeRequestBuilder.BuildProbeRequest(BaseUrl, ApiKey, ModelName);

        var body = ProbeRequestBuilder.BuildWireBody(request);

        body.Model.Should().Be(ModelName);
        body.Messages.Should().HaveCount(1);
        body.Messages[0].Role.Should().Be("user");
        body.Messages[0].Content.Should().Be("ping");
        body.Tools.Should().HaveCount(1);
        body.Tools[0].Type.Should().Be("function");
        body.Tools[0].Function.Name.Should().Be("ping");
        body.ToolChoice.Should().Be("auto");
        body.MaxTokens.Should().Be(1);
        body.Stream.Should().BeFalse();
    }
}
