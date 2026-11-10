using EmailAgent.Contracts.AgentEngines;
using EmailAgent.Infrastructure.Agents;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Agents;

/// <summary>M3T4 — <see cref="TokenCounts"/> total fallback and usage-presence invariants (task-named tests).</summary>
[Trait("Category", "M3T4")]
public class TokenCountsTests
{
    [Fact]
    public void TokenCounts_DefaultsTotalToPromptPlusCompletion()
    {
        var tokens = new TokenCounts(10, 20);

        tokens.EffectiveTotalTokens.Should().Be(30, "an unreported total falls back to prompt + completion");
        tokens.HasUsage.Should().BeTrue();
    }

    [Fact]
    public void TokenCounts_ExplicitTotalWins()
    {
        var tokens = new TokenCounts(10, 20, 35);

        tokens.EffectiveTotalTokens.Should().Be(35, "an explicit provider total must not be recomputed");
        tokens.TotalTokens.Should().Be(35);
    }

    [Fact]
    public void TokenCounts_NegativeCounts_EffectiveTotalTreatedAsZero()
    {
        var negativePrompt = new TokenCounts(-100, 50);
        var negativeTotal = new TokenCounts(10, 20, -30);

        negativePrompt.EffectiveTotalTokens.Should().Be(
            50, "the effective total clamps like the cost derivation — no negative token metrics");
        negativeTotal.EffectiveTotalTokens.Should().Be(
            0, "an explicit but bogus negative total is clamped, not emitted");
    }

    [Fact]
    public void TokenCounts_FromAgentDone_CarriesNullsWhenUsageAbsent()
    {
        var absent = TokenCounts.From(new AgentDone(null, null));
        var absentEvent = TokenCounts.From(null);
        var reported = TokenCounts.From(new AgentDone(10, 20) { TotalTokens = 35 });

        absent.HasUsage.Should().BeFalse(
            "null counts mean the provider reported no usage — the state DeriveCost must see");
        absent.EffectiveTotalTokens.Should().Be(0);
        absentEvent.HasUsage.Should().BeFalse("a caller with no done event has no usage either");
        reported.HasUsage.Should().BeTrue();
        reported.PromptTokens.Should().Be(10);
        reported.CompletionTokens.Should().Be(20);
        reported.EffectiveTotalTokens.Should().Be(35);
    }
}
