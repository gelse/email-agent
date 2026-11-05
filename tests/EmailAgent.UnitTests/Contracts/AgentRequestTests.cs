using System.Text.Json;
using EmailAgent.Contracts.AgentEngines;
using EmailAgent.Contracts.MessageSources;
using FluentAssertions;
using Json.Schema;
using Xunit;

namespace EmailAgent.UnitTests.Contracts;

/// <summary>
/// M2T1 step 15: <see cref="AgentRequest"/> JSON round-trip including the
/// <c>allowed_action_schemas</c> map of <see cref="JsonSchema"/> documents
/// (§9 — the SPA/engine maps each to its tool format in M3).
/// </summary>
public class AgentRequestTests
{
    [Fact]
    public void AgentRequest_RoundTripsJson_IncludingActionSchemas()
    {
        var message = new RemoteMessage(
            "1", DateTimeOffset.UnixEpoch, new Dictionary<string, string>(), "body", []);
        var context = new AgentContext(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        var request = new AgentRequest(
            Message: message,
            Prompt: "summarise",
            AllowedActionSchemas: new Dictionary<string, JsonSchema>
            {
                ["imap-reply"] = JsonSchema.FromText("""{"type":"object"}"""),
            },
            Context: context);

        var json = JsonSerializer.Serialize(request);

        json.Should().Contain("\"message\":");
        json.Should().Contain("\"prompt\":\"summarise\"");
        json.Should().Contain("\"allowed_action_schemas\":{\"imap-reply\":{\"type\":\"object\"}}");
        json.Should().Contain("\"context\":");
        json.Should().NotContain("\"ct\"");

        var back = JsonSerializer.Deserialize<AgentRequest>(json);

        back.Should().NotBeNull();
        back!.Prompt.Should().Be("summarise");
        back.Message.RemoteUid.Should().Be("1");
        back.Message.BodyText.Should().Be("body");
        back.Context.UserId.Should().Be(context.UserId);
        back.Context.SourceId.Should().Be(context.SourceId);
        back.Context.Ct.CanBeCanceled.Should().BeFalse();
        back.AllowedActionSchemas.Keys.Should().Equal(new[] { "imap-reply" });
        JsonSerializer.Serialize(back.AllowedActionSchemas["imap-reply"])
            .Should().Be(JsonSerializer.Serialize(request.AllowedActionSchemas["imap-reply"]));
    }

    [Fact]
    public void AgentRequest_MissingRequiredMember_ThrowsInsteadOfNull()
    {
        // Every member is [JsonRequired]: partial input must fail loudly
        // instead of materialising null into non-nullable members.
        var act = () => JsonSerializer.Deserialize<AgentRequest>("{}");

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void AgentContext_MissingRequiredMembers_ThrowInsteadOfDefault()
    {
        // The nested request context object is held to the same [JsonRequired]
        // contract: {"context":{}} must not yield Guid.Empty identities.
        var act = () => JsonSerializer.Deserialize<AgentContext>("{}");

        act.Should().Throw<JsonException>();
    }
}
