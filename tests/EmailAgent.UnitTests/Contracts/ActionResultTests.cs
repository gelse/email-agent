using System.Text.Json;
using EmailAgent.Contracts.ActionHandlers;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Contracts;

/// <summary>
/// M2T1 step 8/15: <see cref="ActionResult"/> / <see cref="SideEffect"/> /
/// <see cref="ActionContext"/> JSON round-trip with the §10 wire shape
/// (<c>ok</c>/<c>error</c>/<c>side_effects</c>) and the ignored call-scoped
/// <c>Ct</c> token.
/// </summary>
public class ActionResultTests
{
    [Fact]
    public void ActionResult_Without_SideEffects_RoundTripsJson()
    {
        var json = JsonSerializer.Serialize(new ActionResult(true));

        json.Should().Be("{\"ok\":true,\"error\":null,\"side_effects\":null}");

        var back = JsonSerializer.Deserialize<ActionResult>(json);

        back.Should().NotBeNull();
        back!.Ok.Should().BeTrue();
        back.Error.Should().BeNull();
        back.SideEffects.Should().BeNull();
    }

    [Fact]
    public void ActionResult_With_SideEffects_RoundTripsJson()
    {
        var result = new ActionResult(
            Ok: false,
            Error: "smtp send failed: connection refused",
            SideEffects:
            [
                new SideEffect("sent_email", "user@example.com", "{\"subject\":\"hi\"}"),
                new SideEffect("sent_telegram", "chat:42", null),
            ]);

        var json = JsonSerializer.Serialize(result);

        json.Should().Contain("\"ok\":false");
        json.Should().Contain("\"error\":\"smtp send failed: connection refused\"");
        json.Should().Contain("\"kind\":\"sent_email\"");
        json.Should().Contain("\"target\":\"user@example.com\"");
        json.Should().Contain("\"kind\":\"sent_telegram\"");
        json.Should().Contain("\"detail\":null");
        // The Detail value itself is asserted after the round-trip below:
        // System.Text.Json's default encoder may escape the embedded quotes
        // (`\u0022`) — semantically identical JSON, byte-level assertions
        // would be encoder-dependent.

        var back = JsonSerializer.Deserialize<ActionResult>(json);

        back.Should().NotBeNull();
        back!.Ok.Should().BeFalse();
        back.Error.Should().Be("smtp send failed: connection refused");
        back.SideEffects!.Should().HaveCount(2);
        back.SideEffects!.Should().Equal(result.SideEffects!);
        back.SideEffects![0].Detail.Should().Be("{\"subject\":\"hi\"}");
        back.SideEffects![1].Detail.Should().BeNull();
    }

    [Fact]
    public void ActionContext_RoundTripsJson_And_IgnoresCt()
    {
        var ctx = new ActionContext(
            Guid.NewGuid(), Guid.NewGuid(), new CancellationToken(canceled: true));

        var json = JsonSerializer.Serialize(ctx);

        json.Should().Be(
            $"{{\"user_id\":\"{ctx.UserId}\",\"processed_message_id\":\"{ctx.ProcessedMessageId}\"}}");
        json.Should().NotContain("\"ct\"");

        var back = JsonSerializer.Deserialize<ActionContext>(json);

        back.Should().NotBeNull();
        back!.UserId.Should().Be(ctx.UserId);
        back.ProcessedMessageId.Should().Be(ctx.ProcessedMessageId);
        back.Ct.CanBeCanceled.Should().BeFalse(); // default token — call context is not data
    }

    [Fact]
    public void SideEffect_MissingKind_ThrowsInsteadOfNull()
    {
        // [JsonRequired]: Kind is non-nullable and persisted into
        // pending_actions.result_json (§6) — partial input must fail loudly.
        var act = () => JsonSerializer.Deserialize<SideEffect>("{\"target\":\"user@example.com\"}");

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void ActionContext_MissingRequiredMember_ThrowsInsteadOfDefault()
    {
        // Identity members are [JsonRequired]: an empty context object must not
        // materialise Guid.Empty identities that silently no-op downstream.
        var act = () => JsonSerializer.Deserialize<ActionContext>("{}");

        act.Should().Throw<JsonException>();
    }
}
