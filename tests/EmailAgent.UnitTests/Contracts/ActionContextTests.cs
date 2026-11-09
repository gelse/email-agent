using System.Text.Json;
using EmailAgent.Contracts.ActionHandlers;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Contracts;

/// <summary>
/// M4T1: additive <see cref="ActionContext"/> properties (Q-CE6 / SQ-M4-4)
/// — defaults preserved for the M2 wire shape, round-trip when populated.
/// </summary>
public class ActionContextTests
{
    [Fact]
    public void Defaults_DryRunFalse_SourceMessageNull()
    {
        var ctx = new ActionContext(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        ctx.DryRun.Should().BeFalse();
        ctx.SourceMessage.Should().BeNull();
    }

    [Fact]
    public void AdditiveProperties_RoundTrip()
    {
        var snap = new SourceMessageSnapshot(
            RemoteUid: "42",
            MessageId: "<m@example>",
            Subject: "Hello",
            From: "alice@example.com",
            ReplyTo: "reply@example.com",
            To: "bob@example.com",
            References: "<r@example>",
            BodyText: "body");

        var ctx = new ActionContext(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)
        {
            DryRun = true,
            SourceMessage = snap,
        };

        var json = JsonSerializer.Serialize(ctx);
        json.Should().Contain("\"dry_run\":true");
        json.Should().Contain("\"source_message\"");
        json.Should().Contain("\"remote_uid\":\"42\"");

        var back = JsonSerializer.Deserialize<ActionContext>(json);
        back.Should().NotBeNull();
        back!.DryRun.Should().BeTrue();
        back.SourceMessage.Should().Be(snap);
    }

    [Fact]
    public void SourceMessageSnapshot_RoundTrips()
    {
        var snap = new SourceMessageSnapshot("1", "<id>", "Subj", "f@x", "r@x", "t@x", "<ref>", "text");
        var json = JsonSerializer.Serialize(snap);
        var back = JsonSerializer.Deserialize<SourceMessageSnapshot>(json);
        back.Should().Be(snap);
    }

    [Fact]
    public void SideEffectKinds_Values()
    {
        SideEffectKinds.WouldSendEmail.Should().Be("would_send_email");
        SideEffectKinds.SentEmail.Should().Be("sent_email");
    }
}
