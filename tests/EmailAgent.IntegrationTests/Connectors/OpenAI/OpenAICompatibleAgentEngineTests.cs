using System.Text.Json;
using EmailAgent.Contracts.AgentEngines;
using EmailAgent.Contracts.MessageSources;
using EmailAgent.Connectors.OpenAI;
using EmailAgent.IntegrationTests.Fixtures;
using FluentAssertions;
using Json.Schema;
using Xunit;

namespace EmailAgent.IntegrationTests.Connectors.OpenAI;

/// <summary>
/// M3T1 (task Acceptance criteria): the <see cref="OpenAICompatibleAgentEngine"/>
/// against the in-process <see cref="OpenAIMock"/> — non-streamed text + token
/// usage, streamed chunks before the terminal <see cref="AgentDone"/>, the
/// typed <see cref="EngineCallException"/> on HTTP 401, and honored cancellation
/// mid-stream. No socket, no container, no real provider (AGENTS.md #5); the
/// mock only ever sees the recorded <c>sk-test-redacted</c> key marker.
/// </summary>
[Trait("Category", "M3T1")]
[Collection("Postgres")]
public class OpenAICompatibleAgentEngineTests(OpenAIMockFixture fixture) : IClassFixture<OpenAIMockFixture>
{
    [Fact]
    public async Task OpenAICompatibleAgentEngine_CompleteAsync_HappyNonStreaming_ReturnsTextAndTokens()
    {
        await fixture.SetScenarioAsync("happy");
        var engine = new OpenAICompatibleAgentEngine(fixture.Client);
        var req = Request(fixture.CreateConfig());

        List<AgentEvent> events = await DrainAsync(
            engine.CompleteAsync(req, CancellationToken.None));

        events.Should().HaveCount(2, "the recorded happy body maps to exactly the text chunk then done");
        events[0].Should().BeOfType<AgentTextChunk>().Which.Text.Should().Be("hello world");
        var done = events[1].Should().BeOfType<AgentDone>().Subject;
        done.PromptTokens.Should().Be(5);
        done.CompletionTokens.Should().Be(2);
        done.TotalTokens.Should().Be(7, "SQ-M3-17 additive usage channel");
        done.ProviderUsage.Should().NotBeNull();
        done.ProviderUsage!.Value.GetProperty("total_tokens").GetInt32().Should().Be(7);
    }

    [Fact]
    public async Task OpenAICompatibleAgentEngine_CompleteAsync_HappyStreaming_YieldsMultipleTextChunksAndDone()
    {
        await fixture.SetScenarioAsync("happy");
        var engine = new OpenAICompatibleAgentEngine(fixture.Client);
        var req = Request(fixture.CreateConfig());

        List<AgentEvent> events = await DrainAsync(
            engine.CompleteAsync(req, new EngineCallOptions(Stream: true), CancellationToken.None));

        events.OfType<AgentTextChunk>().Should().NotBeEmpty(
            "the streamed happy scenario yields the text in at least one chunk before the terminal event");
        string.Concat(events.OfType<AgentTextChunk>().Select(chunk => chunk.Text))
            .Should().Be("hello world");
        events[^1].Should().BeOfType<AgentDone>(
            "the finish_reason frame terminates the stream — errors are never events");
        var done = (AgentDone)events[^1];
        done.PromptTokens.Should().Be(5);
        done.CompletionTokens.Should().Be(2);
        done.TotalTokens.Should().Be(7);
    }

    [Fact]
    public async Task OpenAICompatibleAgentEngine_CompleteAsync_HttpError_ThrowsEngineCallException()
    {
        await fixture.SetScenarioAsync("error");
        var engine = new OpenAICompatibleAgentEngine(fixture.Client);
        var req = Request(fixture.CreateConfig());

        Func<Task> act = async () => await DrainAsync(
            engine.CompleteAsync(req, CancellationToken.None));

        var exception = await act.Should().ThrowAsync<EngineCallException>();
        exception.Which.StatusCode.Should().Be(401);
        exception.Which.ResponseBody.Should().Contain(
            "invalid api key", "the provider's raw error body is preserved for the API boundary (SQ-M3-1)");
        exception.Which.Detail.Should().BeNull("a plain HTTP error carries no parse detail");
    }

    [Fact]
    public async Task OpenAICompatibleAgentEngine_CompleteAsync_Cancellation_Honored()
    {
        await fixture.SetScenarioAsync("happy");
        var engine = new OpenAICompatibleAgentEngine(fixture.Client);
        var req = Request(fixture.CreateConfig());
        using var cts = new CancellationTokenSource();
        List<AgentEvent> events = [];

        Func<Task> act = async () =>
        {
            await foreach (AgentEvent evt in engine.CompleteAsync(
                req, new EngineCallOptions(Stream: true), cts.Token))
            {
                events.Add(evt);
                if (evt is AgentTextChunk)
                {
                    // Cancel while frames are still in flight — the parser must
                    // honor ct before the next read/yield, not drain the stream.
                    cts.Cancel();
                }
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        events.OfType<AgentTextChunk>().Should().NotBeEmpty(
            "cancellation landed mid-stream, after the first chunk arrived");
        events.OfType<AgentDone>().Should().BeEmpty(
            "a cancelled call never reaches the terminal event");
    }

    /// <summary>An engine request aimed at this fixture's mock (SQ-M3-9 prompt, test key only).</summary>
    private static AgentRequest Request(JsonElement config) =>
        new(
            Message: new RemoteMessage(
                "uid-int-1", DateTimeOffset.UnixEpoch, new Dictionary<string, string>(), "body", []),
            Prompt: "SYSTEM:\nYou are terse.\n\nUSER:\nSay hello world",
            AllowedActionSchemas: new Dictionary<string, JsonSchema>(),
            Context: new AgentContext(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)
            {
                EngineConfigJson = config,
            });

    /// <summary>Drains an event stream so async-iterator exceptions reach the assertion.</summary>
    private static async Task<List<AgentEvent>> DrainAsync(IAsyncEnumerable<AgentEvent> source)
    {
        List<AgentEvent> events = [];
        await foreach (AgentEvent evt in source)
        {
            events.Add(evt);
        }

        return events;
    }
}
