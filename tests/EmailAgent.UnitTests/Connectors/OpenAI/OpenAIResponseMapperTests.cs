using System.Text.Json;
using EmailAgent.Contracts.AgentEngines;
using EmailAgent.Connectors.OpenAI;
using EmailAgent.Connectors.OpenAI.Http;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Connectors.OpenAI;

/// <summary>
/// M3T1 steps 7–8 matrix for <see cref="OpenAIResponseMapper"/>: SSE frames and
/// the non-streamed envelope map onto the M2T1 <see cref="AgentEvent"/> sealed
/// hierarchy verbatim (no new variant), the additive
/// <see cref="AgentDone.TotalTokens"/> / <see cref="AgentDone.ProviderUsage"/>
/// properties carry the provider usage (SQ-M3-17), and an embedded provider
/// error surfaces as <see cref="EngineCallException"/> — never as an event
/// (§9 / Q-CE1 / SQ-M3-1).
/// </summary>
[Trait("Category", "M3T1")]
public class OpenAIResponseMapperTests
{
    [Fact]
    public void OpenAIResponseMapper_MapFrame_TextDelta_YieldsAgentTextChunk()
    {
        var frame = new SseFrame(
            string.Empty,
            """{"choices":[{"index":0,"delta":{"role":"assistant","content":"hello"},"finish_reason":null}]}""");

        IReadOnlyList<AgentEvent> events = OpenAIResponseMapper.MapFrame(frame);

        events.Should().ContainSingle().Which.Should().BeOfType<AgentTextChunk>()
            .Which.Text.Should().Be("hello");
    }

    [Fact]
    public void OpenAIResponseMapper_MapFrame_ToolCall_YieldsAgentToolCall_WithArguments()
    {
        var frame = new SseFrame(
            string.Empty,
            """
            {"choices":[{"index":0,"delta":{"tool_calls":[
              {"index":0,"function":{"name":"ping","arguments":"{\"target\":\"inbox\"}"}}
            ]}}]}
            """);

        IReadOnlyList<AgentEvent> events = OpenAIResponseMapper.MapFrame(frame);

        events.Should().ContainSingle().Which.Should().BeOfType<AgentToolCall>()
            .Which.Arguments.ValueKind.Should().Be(
                JsonValueKind.Object, "OpenAI emits arguments as a JSON string; the mapper parses it");
        var toolCall = events[0].Should().BeOfType<AgentToolCall>().Subject;
        toolCall.Name.Should().Be("ping");
        toolCall.Arguments.GetProperty("target").GetString().Should().Be("inbox");
    }

    [Fact]
    public void OpenAIResponseMapper_MapFrame_FinishWithUsage_YieldsAgentDone()
    {
        var frame = new SseFrame(
            string.Empty,
            """
            {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":12,"completion_tokens":34,"total_tokens":46}}
            """);

        IReadOnlyList<AgentEvent> events = OpenAIResponseMapper.MapFrame(frame);

        events.Should().ContainSingle().Which.Should().BeOfType<AgentDone>();
        var done = (AgentDone)events[0];
        done.PromptTokens.Should().Be(12);
        done.CompletionTokens.Should().Be(34);
        done.TotalTokens.Should().Be(46, "SQ-M3-17 additive usage channel");
        done.ProviderUsage.Should().NotBeNull();
        done.ProviderUsage!.Value.GetProperty("total_tokens").GetInt32().Should().Be(46);
    }

    [Fact]
    public void OpenAIResponseMapper_MapFrame_FinishWithoutUsage_YieldsNullCounts()
    {
        var frame = new SseFrame(
            string.Empty, """{"choices":[{"delta":{},"finish_reason":"stop"}]}""");

        IReadOnlyList<AgentEvent> events = OpenAIResponseMapper.MapFrame(frame);

        var done = events.Should().ContainSingle().Subject.Should().BeOfType<AgentDone>().Subject;
        done.PromptTokens.Should().BeNull();
        done.CompletionTokens.Should().BeNull();
        done.TotalTokens.Should().BeNull();
        done.ProviderUsage.Should().BeNull();
    }

    [Fact]
    public void OpenAIResponseMapper_MapFrame_RoleOnlyAndDoneSentinel_YieldNothing()
    {
        var roleOnly = new SseFrame(string.Empty, """{"choices":[{"delta":{"role":"assistant"}}]}""");
        var doneSentinel = new SseFrame(string.Empty, "[DONE]");
        var emptyData = new SseFrame(string.Empty, "   ");

        OpenAIResponseMapper.MapFrame(roleOnly).Should().BeEmpty();
        OpenAIResponseMapper.MapFrame(doneSentinel).Should().BeEmpty();
        OpenAIResponseMapper.MapFrame(emptyData).Should().BeEmpty();
    }

    [Fact]
    public void OpenAIResponseMapper_MapFrame_ChoicelessFrameWithoutUsage_YieldsNothing()
    {
        // A keep-alive / bookkeeping frame with `"choices": []` and no usage
        // block must not be mistaken for a terminal event (the usage-bearing
        // tail is the only choiceless frame that maps to AgentDone).
        var frame = new SseFrame(string.Empty, """{"object":"chat.completion.chunk","choices":[]}""");

        OpenAIResponseMapper.MapFrame(frame).Should().BeEmpty();
    }

    [Fact]
    public void OpenAIResponseMapper_MapFrame_UsageOnlyTailFrame_YieldsAgentDoneWithUsage()
    {
        // The `stream_options.include_usage` tail chunk: no choices at all,
        // only the final usage block — it must surface as the terminal
        // AgentDone instead of dropping the token counts.
        var frame = new SseFrame(
            string.Empty,
            """
            {"id":"cmpl-x","object":"chat.completion.chunk","choices":[],
             "usage":{"prompt_tokens":12,"completion_tokens":34,"total_tokens":46}}
            """);

        IReadOnlyList<AgentEvent> events = OpenAIResponseMapper.MapFrame(frame);

        var done = events.Should().ContainSingle().Subject.Should().BeOfType<AgentDone>().Subject;
        done.PromptTokens.Should().Be(12);
        done.CompletionTokens.Should().Be(34);
        done.TotalTokens.Should().Be(46);
        done.ProviderUsage.Should().NotBeNull();
        done.ProviderUsage!.Value.GetProperty("total_tokens").GetInt32().Should().Be(46);
    }

    [Fact]
    public void OpenAIResponseMapper_MapFrame_NonObjectChoice_ThrowsEngineCallException()
    {
        var frame = new SseFrame(string.Empty, """{"choices":["not-an-object"]}""");

        Action act = () => OpenAIResponseMapper.MapFrame(frame);

        act.Should().Throw<EngineCallException>()
            .Where(e => e.StatusCode == 200
                && e.Detail == "choices[0] must be a JSON object, was String")
            .WithMessage("*choices[0] must be a JSON object*");
    }

    [Fact]
    public void OpenAIResponseMapper_MapFrame_EmbeddedError_ThrowsEngineCallException()
    {
        var frame = new SseFrame(
            string.Empty, """{"error":{"message":"invalid api key","type":"invalid_request_error"}}""");

        Action act = () => OpenAIResponseMapper.MapFrame(frame);

        act.Should().Throw<EngineCallException>()
            .Where(e => e.StatusCode == 200 && e.Detail!.Contains("invalid api key", StringComparison.Ordinal))
            .WithMessage("*provider reported an error*");
    }

    [Fact]
    public void OpenAIResponseMapper_MapFrame_MalformedJson_ThrowsEngineCallException()
    {
        var frame = new SseFrame(string.Empty, "{not json");

        Action act = () => OpenAIResponseMapper.MapFrame(frame);

        act.Should().Throw<EngineCallException>()
            .Where(e => e.StatusCode == 200 && e.ResponseBody == "{not json");
    }

    [Fact]
    public void OpenAIResponseMapper_MapNonStreaming_HappyPath_YieldsAgentTextChunkAndAgentDone()
    {
        using JsonDocument doc = JsonDocument.Parse(
            OpenAIFixtureFiles.Read("happy-non-streaming.json"));

        List<AgentEvent> events = OpenAIResponseMapper.MapNonStreaming(doc).ToList();

        events.Should().HaveCount(2, "no tool_calls: exactly the text chunk then the terminal done");
        events[0].Should().BeOfType<AgentTextChunk>().Which.Text.Should().Be("hello world");
        var done = events[1].Should().BeOfType<AgentDone>().Subject;
        done.PromptTokens.Should().Be(5);
        done.CompletionTokens.Should().Be(2);
        done.TotalTokens.Should().Be(7);
        done.ProviderUsage.Should().NotBeNull();
    }

    [Fact]
    public void OpenAIResponseMapper_MapNonStreaming_ProbePass_ToolCallsArrayPresent()
    {
        List<AgentEvent> events;
        using (JsonDocument doc = JsonDocument.Parse(OpenAIFixtureFiles.Read("probe-pass.json")))
        {
            events = OpenAIResponseMapper.MapNonStreaming(doc).ToList();
        }

        events.Should().HaveCount(3, "one tool call first, then the empty text chunk, then done");
        var toolCall = events[0].Should().BeOfType<AgentToolCall>().Subject;
        toolCall.Name.Should().Be("ping");
        toolCall.Arguments.ValueKind.Should().Be(JsonValueKind.Object, "an empty arguments string parses to {}");
        toolCall.Arguments.GetRawText().Should().Be("{}");
        events[1].Should().BeOfType<AgentTextChunk>().Which.Text.Should().BeEmpty(
            "a null content maps to an empty text chunk");
        var done = events[2].Should().BeOfType<AgentDone>().Subject;
        done.PromptTokens.Should().Be(4);
        done.CompletionTokens.Should().Be(6);
        done.TotalTokens.Should().Be(10);

        // The usage element must have been cloned out of the (now disposed) document.
        done.ProviderUsage!.Value.GetProperty("total_tokens").GetInt32().Should().Be(10);
    }

    [Fact]
    public void OpenAIResponseMapper_MapNonStreaming_EmbeddedError_ThrowsEngineCallException()
    {
        using JsonDocument doc = JsonDocument.Parse(
            OpenAIFixtureFiles.Read("error-bad-request.json"));

        Action act = () => OpenAIResponseMapper.MapNonStreaming(doc).ToList();

        act.Should().Throw<EngineCallException>()
            .Where(e => e.StatusCode == 200 && e.Detail!.Contains("invalid api key", StringComparison.Ordinal))
            .WithMessage("*provider reported an error*");
    }

    [Fact]
    public void OpenAIResponseMapper_MapNonStreaming_MissingChoices_ThrowsEngineCallException()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"id":"cmpl-x","object":"chat.completion"}""");

        Action act = () => OpenAIResponseMapper.MapNonStreaming(doc).ToList();

        act.Should().Throw<EngineCallException>()
            .Where(e => e.StatusCode == 200 && e.Detail == "response body has no choices[0]");
    }

    [Fact]
    public void OpenAIResponseMapper_MapNonStreaming_NonObjectChoice_ThrowsEngineCallException()
    {
        using JsonDocument doc = JsonDocument.Parse(
            """{"id":"cmpl-x","object":"chat.completion","choices":["not-an-object"]}""");

        Action act = () => OpenAIResponseMapper.MapNonStreaming(doc).ToList();

        act.Should().Throw<EngineCallException>()
            .Where(e => e.StatusCode == 200
                && e.Detail == "choices[0] must be a JSON object, was String")
            .WithMessage("*choices[0] must be a JSON object*");
    }
}
