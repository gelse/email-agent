using EmailAgent.Api.Models;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Models;

/// <summary>M3T2 — <see cref="ProbeResult"/> factory invariants (task-named tests).</summary>
[Trait("Category", "M3T2")]
public class ProbeResultTests
{
    private static readonly DateTimeOffset CachedAt = new(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ProbeResult_PassFromCache_PreservesCachedFromTimestamp()
    {
        var result = ProbeResult.PassFromCacheResult(CachedAt, Now);

        result.Outcome.Should().Be(ProbeOutcome.PassFromCache);
        result.CachedFrom.Should().Be(
            CachedAt, "the honored probe's own timestamp must survive — it is what M3T3 re-persists");
        result.ProbedAt.Should().Be(Now);
        result.Reason.Should().BeNull("a cache hit has no failure reason");
        result.RequestExcerpt.Should().BeEmpty("no request ran");
        result.ResponseExcerpt.Should().BeEmpty("no response ran");
    }

    [Fact]
    public void ProbeResult_Fail_CarriesReasonAndExcerpts()
    {
        var result = ProbeResult.FailResult(
            "no_tool_calls",
            requestExcerpt: "{\"model\":\"m\"}",
            responseExcerpt: "{\"choices\":[]}",
            probedAt: Now);

        result.Outcome.Should().Be(ProbeOutcome.Fail);
        result.Reason.Should().Be("no_tool_calls");
        result.RequestExcerpt.Should().Be("{\"model\":\"m\"}");
        result.ResponseExcerpt.Should().Be("{\"choices\":[]}");
        result.ProbedAt.Should().Be(Now);
        result.CachedFrom.Should().BeNull("a fresh probe never carries a cached-from timestamp");
    }

    [Fact]
    public void ProbeResult_Pass_CarriesExcerptsWithoutReasonOrCachedFrom()
    {
        var result = ProbeResult.PassResult("req", "resp", Now);

        result.Outcome.Should().Be(ProbeOutcome.Pass);
        result.Reason.Should().BeNull();
        result.RequestExcerpt.Should().Be("req");
        result.ResponseExcerpt.Should().Be("resp");
        result.CachedFrom.Should().BeNull();
    }
}
