using System.Text.Json;
using EmailAgent.Infrastructure.Agents;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Agents;

/// <summary>M3T4 — <see cref="ProviderCostExtractor"/> surface readers (task-named tests).</summary>
[Trait("Category", "M3T4")]
public class ProviderCostExtractorTests
{
    [Fact]
    public void ProviderCostExtractor_Header_CaseInsensitive()
    {
        IReadOnlyDictionary<string, string> headers =
            new Dictionary<string, string> { ["X-LiteLLM-Cost"] = "0.0007" };

        ProviderCostExtractor.ExtractFromHeader(headers, "x-litellm-cost")
            .Should().Be(0.0007m, "header matching must ignore case in both directions");
        ProviderCostExtractor.ExtractFromHeader(headers, "X-LITELLM-COST")
            .Should().Be(0.0007m, "the header name may differ in case from the dictionary key");
    }

    [Fact]
    public void ProviderCostExtractor_Header_UnparseableString_ReturnsNull()
    {
        IReadOnlyDictionary<string, string> headers =
            new Dictionary<string, string> { ["x-litellm-cost"] = "not-a-number" };

        ProviderCostExtractor.ExtractFromHeader(headers, "x-litellm-cost")
            .Should().BeNull("an unparseable header must degrade to 'cost unknown', never throw");
    }

    [Fact]
    public void ProviderCostExtractor_Header_MissingOrNullDictionary_ReturnsNull()
    {
        IReadOnlyDictionary<string, string> headers =
            new Dictionary<string, string> { ["x-litellm-cost"] = "0.0007" };

        ProviderCostExtractor.ExtractFromHeader(headers, "other-header").Should().BeNull();
        ProviderCostExtractor.ExtractFromHeader(null, "x-litellm-cost").Should().BeNull();
        ProviderCostExtractor.ExtractFromHeader(
                new Dictionary<string, string> { ["x-litellm-cost"] = "" }, "x-litellm-cost")
            .Should().BeNull("an empty header value carries no cost");
    }

    [Fact]
    public void ProviderCostExtractor_Header_InvariantCultureParses()
    {
        IReadOnlyDictionary<string, string> headers =
            new Dictionary<string, string> { ["x-litellm-cost"] = "0.0007" };
        IReadOnlyDictionary<string, string> exponent =
            new Dictionary<string, string> { ["x-litellm-cost"] = "1.5e-4" };

        ProviderCostExtractor.ExtractFromHeader(headers, "x-litellm-cost")
            .Should().Be(0.0007m, "parsing is invariant-culture — period decimal separator");
        ProviderCostExtractor.ExtractFromHeader(exponent, "x-litellm-cost")
            .Should().Be(0.00015m, "exponent notation is part of the accepted grammar (NumberStyles.Float)");
    }

    [Fact]
    public void ProviderCostExtractor_GroupSeparatorRejected_ReturnsNull()
    {
        // NumberStyles.Number would read "0,0007" as the thousands-grouped 7 —
        // a 10 000x overstatement. Float rejects it so the caller falls
        // through to the next cost rule instead.
        IReadOnlyDictionary<string, string> headers =
            new Dictionary<string, string> { ["x-litellm-cost"] = "0,0007" };
        using var usage = JsonDocument.Parse("""{"cost":"0,0007"}""");

        ProviderCostExtractor.ExtractFromHeader(headers, "x-litellm-cost")
            .Should().BeNull("a comma-decimal locale mangle must degrade, never parse as 7");
        ProviderCostExtractor.ExtractFromUsage(usage.RootElement)
            .Should().BeNull("the string cost path applies the same grammar");
    }

    [Fact]
    public void ProviderCostExtractor_Usage_Missing_ReturnsNull()
    {
        ProviderCostExtractor.ExtractFromUsage(null).Should().BeNull("no usage block means no usage cost");

        using var doc = JsonDocument.Parse("""{"prompt_tokens":100,"completion_tokens":50}""");
        ProviderCostExtractor.ExtractFromUsage(doc.RootElement)
            .Should().BeNull("a usage block without a cost field means the provider reported none");
    }

    [Fact]
    public void ProviderCostExtractor_Usage_NotAnObject_ReturnsNull()
    {
        using var doc = JsonDocument.Parse("\"just-a-string\"");
        ProviderCostExtractor.ExtractFromUsage(doc.RootElement).Should().BeNull();
    }

    [Fact]
    public void ProviderCostExtractor_Usage_CostAsNumberOrString_BothParse()
    {
        using var numeric = JsonDocument.Parse("""{"cost":0.0007}""");
        ProviderCostExtractor.ExtractFromUsage(numeric.RootElement).Should().Be(0.0007m);

        using var textual = JsonDocument.Parse("""{"cost":"0.0007"}""");
        ProviderCostExtractor.ExtractFromUsage(textual.RootElement)
            .Should().Be(0.0007m, "some providers serialize the cost as a string");
    }

    [Fact]
    public void ProviderCostExtractor_Usage_CostUnparseableKind_ReturnsNull()
    {
        using var textual = JsonDocument.Parse("""{"cost":"free"}""");
        ProviderCostExtractor.ExtractFromUsage(textual.RootElement).Should().BeNull();

        using var boolean = JsonDocument.Parse("""{"cost":true}""");
        ProviderCostExtractor.ExtractFromUsage(boolean.RootElement)
            .Should().BeNull("a non-numeric cost degrades to 'cost unknown'");
    }

    [Fact]
    public void ProviderCostExtractor_Usage_NumberNotRepresentable_ReturnsNull()
    {
        // GetDecimal() would throw FormatException on both of these — the
        // "never throws, degrades to null" contract requires TryGetDecimal.
        using var outOfRange = JsonDocument.Parse("""{"cost":1e30}""");
        ProviderCostExtractor.ExtractFromUsage(outOfRange.RootElement)
            .Should().BeNull("a cost beyond decimal range must degrade, not throw");

        using var absurd = JsonDocument.Parse("""{"cost":79228162514264337593543950336}""");
        ProviderCostExtractor.ExtractFromUsage(absurd.RootElement)
            .Should().BeNull("decimal's exact max + 1 has no representation");
    }
}
