using System.Text.Json;
using EmailAgent.Contracts.AgentEngines;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Contracts;

/// <summary>
/// M2T1 step 11/15: the sealed <see cref="AgentEvent"/> hierarchy round-trips
/// through the polymorphic converter with the verbatim §9 kind discriminator
/// (<c>text</c>/<c>tool_call</c>/<c>done</c>) — for the declared base type
/// <b>and</b> for concrete static types — and the <c>arguments</c> object is
/// enforced on both read and write.
/// </summary>
public class AgentEventTests
{
    [Fact]
    public void AgentEvent_TextChunk_RoundTripsPolymorphically_And_UnknownKindThrows()
    {
        AgentEvent evt = new AgentTextChunk("hello");

        var json = JsonSerializer.Serialize(evt);

        json.Should().Be("{\"kind\":\"text\",\"text\":\"hello\"}");

        // Concrete static types resolve the same converter (the factory
        // attribute sits on every sealed record) instead of falling back to
        // the default PascalCase record shape.
        JsonSerializer.Serialize(new AgentTextChunk("hello")).Should().Be(json);
        JsonSerializer.Deserialize<AgentTextChunk>(json)!.Text.Should().Be("hello");

        var back = JsonSerializer.Deserialize<AgentEvent>(json);

        back.Should().BeOfType<AgentTextChunk>();
        back!.Kind.Should().Be("text");
        ((AgentTextChunk)back).Text.Should().Be("hello");

        // Unknown discriminators are rejected, not silently mapped.
        var act = () => JsonSerializer.Deserialize<AgentEvent>("{\"kind\":\"nope\"}");
        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void AgentEvent_ToolCall_And_Done_RoundTrip()
    {
        AgentEvent toolCall = new AgentToolCall(
            "imap-reply",
            JsonDocument.Parse("{\"to\":\"a@example.com\",\"body\":\"hi\"}").RootElement.Clone());

        var toolCallJson = JsonSerializer.Serialize(toolCall);

        toolCallJson.Should().Be(
            "{\"kind\":\"tool_call\",\"name\":\"imap-reply\"," +
            "\"arguments\":{\"to\":\"a@example.com\",\"body\":\"hi\"}}");

        var toolCallBack = JsonSerializer.Deserialize<AgentEvent>(toolCallJson);

        toolCallBack.Should().BeOfType<AgentToolCall>();
        var parsedToolCall = (AgentToolCall)toolCallBack!;
        parsedToolCall.Kind.Should().Be("tool_call");
        parsedToolCall.Name.Should().Be("imap-reply");
        parsedToolCall.Arguments.GetProperty("to").GetString().Should().Be("a@example.com");
        parsedToolCall.Arguments.GetProperty("body").GetString().Should().Be("hi");

        AgentEvent done = new AgentDone(PromptTokens: 42, CompletionTokens: null);

        var doneJson = JsonSerializer.Serialize(done);

        doneJson.Should().Be("{\"kind\":\"done\",\"prompt_tokens\":42,\"completion_tokens\":null}");

        var doneBack = JsonSerializer.Deserialize<AgentEvent>(doneJson);

        doneBack.Should().BeOfType<AgentDone>();
        var parsedDone = (AgentDone)doneBack!;
        parsedDone.Kind.Should().Be("done");
        parsedDone.PromptTokens.Should().Be(42);
        parsedDone.CompletionTokens.Should().BeNull();
    }

    [Fact]
    public void AgentEvent_ToolCall_ConcreteStaticType_And_ArgumentsOutliveSourceDocument()
    {
        var argsDoc = JsonDocument.Parse("{\"to\":\"a@example.com\"}");
        var toolCall = new AgentToolCall("imap-reply", argsDoc.RootElement.Clone());
        argsDoc.Dispose(); // cloned arguments must survive the source document

        var json = JsonSerializer.Serialize(toolCall); // static type = AgentToolCall

        json.Should().Be(
            "{\"kind\":\"tool_call\",\"name\":\"imap-reply\",\"arguments\":{\"to\":\"a@example.com\"}}");

        var back = JsonSerializer.Deserialize<AgentToolCall>(json);

        back.Should().NotBeNull();
        back!.Kind.Should().Be("tool_call");
        back.Name.Should().Be("imap-reply");
        back.Arguments.GetProperty("to").GetString().Should().Be("a@example.com");
    }

    [Fact]
    public void AgentEvent_ToolCall_RequiresObjectArguments_OnReadAndWrite()
    {
        // Read side: anything but a JSON object is rejected.
        foreach (var bad in new[] { "[]", "null", "\"{}\"", "42" })
        {
            var readAct = () => JsonSerializer.Deserialize<AgentEvent>(
                $"{{\"kind\":\"tool_call\",\"name\":\"x\",\"arguments\":{bad}}}");
            readAct.Should().Throw<JsonException>();
        }

        // Write side: a JsonElement holding non-object JSON is rejected too,
        // so what we write is always what Read accepts.
        using var nullDoc = JsonDocument.Parse("null");
        var writeAct = () => JsonSerializer.Serialize(
            new AgentToolCall("x", nullDoc.RootElement.Clone()));
        writeAct.Should().Throw<JsonException>();

        // default(JsonElement) normalises to an empty object.
        var undefinedJson = JsonSerializer.Serialize(new AgentToolCall("x", default));
        undefinedJson.Should().Be(
            "{\"kind\":\"tool_call\",\"name\":\"x\",\"arguments\":{}}");
        JsonSerializer.Deserialize<AgentToolCall>(undefinedJson)!.Arguments.ValueKind
            .Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public void AgentEvent_Done_WithNullTokens_RoundTrips_And_ConcreteKindMismatchThrows()
    {
        AgentEvent done = new AgentDone(PromptTokens: null, CompletionTokens: null);

        var json = JsonSerializer.Serialize(done);

        json.Should().Be(
            "{\"kind\":\"done\",\"prompt_tokens\":null,\"completion_tokens\":null}");

        var back = JsonSerializer.Deserialize<AgentEvent>(json);

        back.Should().BeOfType<AgentDone>();
        var parsed = (AgentDone)back!;
        parsed.PromptTokens.Should().BeNull();
        parsed.CompletionTokens.Should().BeNull();

        // A concrete static type rejects a mismatched discriminator instead of
        // handing back an uncastable instance.
        var mismatchAct = () => JsonSerializer.Deserialize<AgentDone>(
            "{\"kind\":\"text\",\"text\":\"hi\"}");
        mismatchAct.Should().Throw<JsonException>();
    }
}
