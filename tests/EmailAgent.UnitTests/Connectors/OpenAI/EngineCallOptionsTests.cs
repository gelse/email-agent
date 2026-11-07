using EmailAgent.Connectors.OpenAI;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Connectors.OpenAI;

/// <summary>
/// Q-L15 (M3T2 follow-up fix): the additive plugin-local
/// <see cref="EngineCallOptions.MaxTokens"/> member — the default call shape
/// keeps <c>max_tokens</c> off the wire (unchanged behavior for every
/// existing caller), while an opt-in value rides the per-call options that
/// <see cref="OpenAICompatibleAgentEngine"/> forwards to
/// <c>OpenAIRequestBuilder</c>.
/// </summary>
[Trait("Category", "M3T2")]
public class EngineCallOptionsTests
{
    [Fact]
    public void EngineCallOptions_Default_CarriesNoMaxTokens()
    {
        EngineCallOptions.Default.Stream.Should().BeFalse();
        EngineCallOptions.Default.Timeout.Should().BeNull();
        EngineCallOptions.Default.MaxTokens.Should().BeNull(
            "EngineCallOptions.Default must keep max_tokens off the wire — null means omit (Q-L15)");
    }

    [Fact]
    public void EngineCallOptions_WithMaxTokens_KeepsTheCostBound()
    {
        // The shape M3T2's probe transmits (SQ-M3-3's canonical max_tokens: 1).
        var probeOptions = new EngineCallOptions(Stream: false, MaxTokens: 1);

        probeOptions.MaxTokens.Should().Be(1);
        probeOptions.Stream.Should().BeFalse();
        probeOptions.Timeout.Should().BeNull();

        // The member is additive: Stream/Timeout keep working positionally.
        new EngineCallOptions(Stream: true, Timeout: TimeSpan.FromSeconds(5)).MaxTokens
            .Should().BeNull("callers that do not set MaxTokens stay uncapped");
    }
}
