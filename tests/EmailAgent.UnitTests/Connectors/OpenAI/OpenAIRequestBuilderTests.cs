using System.Text.Json;
using EmailAgent.Connectors.OpenAI;
using EmailAgent.Connectors.OpenAI.Http;
using FluentAssertions;
using Json.Schema;
using Xunit;

namespace EmailAgent.UnitTests.Connectors.OpenAI;

/// <summary>
/// M3T1 step 6 matrix for <see cref="OpenAIRequestBuilder"/>: the canonical
/// OpenAI-compatible request shape, the SQ-M3-9 prompt-splitting convention
/// (with the documented lenient fallback), and the Q-L4 tool translation that
/// only fires when the caller allowed at least one action schema.
/// </summary>
[Trait("Category", "M3T1")]
public class OpenAIRequestBuilderTests
{
    private const string ActionSchemaJson =
        """
        {
          "type": "object",
          "description": "Send an email",
          "properties": { "to": { "type": "string" } }
        }
        """;

    [Fact]
    public void OpenAIRequestBuilder_BuildsOpenAIShape_FromAgentRequest()
    {
        var req = OpenAIFixtureFiles.Request(
            prompt: "SYSTEM:\nYou are terse.\n\nUSER:\nSay hello world",
            allowedActions: new Dictionary<string, JsonSchema>
            {
                ["send_email"] = JsonSchema.FromText(ActionSchemaJson),
            });

        string json = OpenAIRequestBuilder.Build(
            req, OpenAICompatibleConfig.FromJson(OpenAIFixtureFiles.HappyConfig()), stream: false);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        root.GetProperty("model").GetString().Should().Be("gpt-4o-mini");
        root.GetProperty("stream").GetBoolean().Should().BeFalse();
        root.TryGetProperty("stream_options", out _).Should().BeFalse(
            "only streamed calls ask for the usage tail");

        JsonElement messages = root.GetProperty("messages");
        messages.GetArrayLength().Should().Be(2, "the SQ-M3-9 prompt carries both markers");
        messages[0].GetProperty("role").GetString().Should().Be("system");
        messages[0].GetProperty("content").GetString().Should().Be("You are terse.");
        messages[1].GetProperty("role").GetString().Should().Be("user");
        messages[1].GetProperty("content").GetString().Should().Be("Say hello world");

        JsonElement tools = root.GetProperty("tools");
        tools.GetArrayLength().Should().Be(1);
        tools[0].GetProperty("type").GetString().Should().Be("function");
        JsonElement function = tools[0].GetProperty("function");
        function.GetProperty("name").GetString().Should().Be("send_email", "the schema key is the tool name");
        function.GetProperty("description").GetString().Should().Be("Send an email");
        function.GetProperty("parameters").GetProperty("to").GetProperty("type")
            .GetString().Should().Be("string", "parameters is the schema's properties object verbatim");
        root.GetProperty("tool_choice").GetString().Should().Be("auto");
    }

    [Fact]
    public void OpenAIRequestBuilder_WithoutPromptMarkers_SendsWholePromptAsUser()
    {
        var req = OpenAIFixtureFiles.Request(prompt: "just a question");

        string json = OpenAIRequestBuilder.Build(
            req, OpenAICompatibleConfig.FromJson(OpenAIFixtureFiles.HappyConfig()), stream: false);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement messages = doc.RootElement.GetProperty("messages");
        messages.GetArrayLength().Should().Be(1, "the lenient fallback sends one user message");
        messages[0].GetProperty("role").GetString().Should().Be("user");
        messages[0].GetProperty("content").GetString().Should().Be("just a question");
    }

    [Fact]
    public void OpenAIRequestBuilder_WithoutAllowedActions_OmitsToolsEntirely()
    {
        var req = OpenAIFixtureFiles.Request(); // no allowed action schemas

        string json = OpenAIRequestBuilder.Build(
            req, OpenAICompatibleConfig.FromJson(OpenAIFixtureFiles.HappyConfig()), stream: true);

        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("stream").GetBoolean().Should().BeTrue();
        doc.RootElement.TryGetProperty("tools", out _).Should().BeFalse(
            "Q-L4: tools are sent only when AllowedActionSchemas is non-empty");
        doc.RootElement.TryGetProperty("tool_choice", out _).Should().BeFalse();
    }

    [Fact]
    public void OpenAIRequestBuilder_SchemaWithoutDescription_UsesEmptyDescription()
    {
        var req = OpenAIFixtureFiles.Request(
            allowedActions: new Dictionary<string, JsonSchema>
            {
                ["noop"] = JsonSchema.FromText("""{"type":"object"}"""),
            });

        string json = OpenAIRequestBuilder.Build(
            req, OpenAICompatibleConfig.FromJson(OpenAIFixtureFiles.HappyConfig()), stream: false);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement function = doc.RootElement.GetProperty("tools")[0].GetProperty("function");
        function.GetProperty("description").GetString().Should().BeEmpty(
            "an absent description annotation maps to the empty string");
        function.GetProperty("parameters").GetRawText().Should().Be("{}",
            "a schema declaring no properties falls back to the empty parameters object");
    }

    [Fact]
    public void OpenAIRequestBuilder_StreamedRequest_AsksForUsageTail()
    {
        // OpenAI omits `usage` from streamed chunks unless the request carries
        // stream_options.include_usage — without it the terminal merge would
        // never see a usage frame and every streamed completion would report
        // null token counts (M3T1 review WARNING).
        var req = OpenAIFixtureFiles.Request();

        string json = OpenAIRequestBuilder.Build(
            req, OpenAICompatibleConfig.FromJson(OpenAIFixtureFiles.HappyConfig()), stream: true);

        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("stream").GetBoolean().Should().BeTrue();
        JsonElement streamOptions = doc.RootElement.GetProperty("stream_options");
        streamOptions.GetProperty("include_usage").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void OpenAIRequestBuilder_SystemMarkerWithoutUserMarker_FallsBackToUserMessage()
    {
        // A prompt that starts with SYSTEM: but never reaches the USER: marker
        // is NOT split — the documented fallback keeps the text whole instead
        // of silently truncating it.
        var req = OpenAIFixtureFiles.Request(prompt: "SYSTEM:\nonly a system half");

        string json = OpenAIRequestBuilder.Build(
            req, OpenAICompatibleConfig.FromJson(OpenAIFixtureFiles.HappyConfig()), stream: false);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement messages = doc.RootElement.GetProperty("messages");
        messages.GetArrayLength().Should().Be(1);
        messages[0].GetProperty("role").GetString().Should().Be("user");
        messages[0].GetProperty("content").GetString().Should().Be("SYSTEM:\nonly a system half");
    }

    // -----------------------------------------------------------------------
    // Q-L15 (M3T2 follow-up): the additive EngineCallOptions.MaxTokens cap —
    // emitted only when set, never by default.
    // -----------------------------------------------------------------------

    [Fact]
    public void OpenAIRequestBuilder_WithMaxTokens_EmitsCostBound()
    {
        // M3T2's probe passes SQ-M3-3's canonical max_tokens: 1 through
        // EngineCallOptions.MaxTokens — the field must reach the wire body.
        var req = OpenAIFixtureFiles.Request(
            prompt: "ping",
            allowedActions: new Dictionary<string, JsonSchema>
            {
                ["ping"] = JsonSchema.FromText(
                    """{"type":"object","description":"Probe tool; returns nothing.","properties":{},"additionalProperties":false}"""),
            });

        string json = OpenAIRequestBuilder.Build(
            req, OpenAICompatibleConfig.FromJson(OpenAIFixtureFiles.HappyConfig()), stream: false, maxTokens: 1);

        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("max_tokens").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("stream").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void OpenAIRequestBuilder_WithoutMaxTokens_OmitsCostBound()
    {
        // Default/null MaxTokens keeps the field entirely off the wire — the
        // exact pre-Q-L15 request shape every existing caller still gets.
        var req = OpenAIFixtureFiles.Request();

        string nonStreamed = OpenAIRequestBuilder.Build(
            req, OpenAICompatibleConfig.FromJson(OpenAIFixtureFiles.HappyConfig()), stream: false);
        string streamed = OpenAIRequestBuilder.Build(
            req, OpenAICompatibleConfig.FromJson(OpenAIFixtureFiles.HappyConfig()), stream: true);

        using JsonDocument nonStreamedDoc = JsonDocument.Parse(nonStreamed);
        nonStreamedDoc.RootElement.TryGetProperty("max_tokens", out _).Should().BeFalse(
            "an unset MaxTokens must not add max_tokens to the non-streamed body");

        using JsonDocument streamedDoc = JsonDocument.Parse(streamed);
        streamedDoc.RootElement.TryGetProperty("max_tokens", out _).Should().BeFalse(
            "an unset MaxTokens must not add max_tokens to the streamed body either");
    }
}
