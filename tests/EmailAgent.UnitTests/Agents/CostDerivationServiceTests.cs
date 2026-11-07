using System.Text.Json;
using EmailAgent.Infrastructure.Agents;
using EmailAgent.Infrastructure.Entities;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Agents;

/// <summary>
/// M3T4 — <see cref="CostDerivationService"/> three-rule precedence
/// (provider cost &gt; price-derived &gt; NULL; task-named tests per the M3
/// milestone verification rows).
/// </summary>
[Trait("Category", "M3T4")]
public class CostDerivationServiceTests
{
    private const decimal ProviderCost = 0.0007m;

    private readonly ICostDerivationService _service = new CostDerivationService();

    private static readonly IReadOnlyDictionary<string, string> CostHeader =
        new Dictionary<string, string> { ["x-litellm-cost"] = ProviderCost.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    private static ModelCatalogEntry Entry(decimal? inputPrice = null, decimal? outputPrice = null) =>
        new() { PricePer1kInputUsd = inputPrice, PricePer1kOutputUsd = outputPrice };

    private static JsonElement Usage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ---- Rule 1: provider-reported cost -----------------------------------

    [Fact]
    public void CostDerivation_ProviderReportsCostInHeader_UsesHeader()
    {
        // Prices ARE set — the provider value must still win (milestone row 1).
        var entry = Entry(inputPrice: 0.005m, outputPrice: 0.015m);
        var usage = Usage("""{"cost":0.0042}""");

        var cost = _service.DeriveCost(new TokenCounts(100, 50), entry, CostHeader, usage);

        cost.Should().Be(ProviderCost, "the x-litellm-cost header is the highest-precedence cost surface");
    }

    [Fact]
    public void CostDerivation_ProviderReportsCostInUsage_UsesUsage()
    {
        // Header absent, usage.cost present, prices set — usage wins over prices.
        var entry = Entry(inputPrice: 0.005m, outputPrice: 0.015m);
        var usage = Usage($$"""{"cost":{{ProviderCost}}}""");

        var cost = _service.DeriveCost(new TokenCounts(100, 50), entry, providerCostHeaders: null, usage);

        cost.Should().Be(ProviderCost, "usage.cost is the provider's own number when no header reports one");
    }

    [Fact]
    public void CostDerivation_HeaderTakesPrecedenceOverUsage()
    {
        var entry = Entry(inputPrice: 0.005m, outputPrice: 0.015m);
        var usage = Usage("""{"cost":0.0042}""");
        var header = new Dictionary<string, string> { ["x-litellm-cost"] = "0.0002" };

        var cost = _service.DeriveCost(new TokenCounts(100, 50), entry, header, usage);

        cost.Should().Be(0.0002m, "when both provider surfaces report a cost, the header wins");
    }

    // ---- Rule 2: derive from admin-entered prices -------------------------

    [Fact]
    public void CostDerivation_PricesSet_NoProviderCost_DerivesFromPrices()
    {
        var entry = Entry(inputPrice: 0.005m, outputPrice: 0.015m);

        var cost = _service.DeriveCost(new TokenCounts(100, 50), entry, providerCostHeaders: null, null);

        cost.Should().Be(0.00125m, "(100/1000)*0.005 + (50/1000)*0.015 = 0.0005 + 0.00075");
    }

    [Fact]
    public void CostDerivation_PromptTokensOnly_OutputPriceNotSet_ReturnsInputOnlyCost()
    {
        var entry = Entry(inputPrice: 0.005m);

        var cost = _service.DeriveCost(new TokenCounts(100, 50), entry, providerCostHeaders: null, null);

        cost.Should().Be(0.0005m, "only the priced portion — the NULL output price contributes nothing");
    }

    [Fact]
    public void CostDerivation_CompletionTokensOnly_InputPriceNotSet_ReturnsOutputOnlyCost()
    {
        var entry = Entry(outputPrice: 0.015m);

        var cost = _service.DeriveCost(new TokenCounts(100, 50), entry, providerCostHeaders: null, null);

        cost.Should().Be(0.00075m, "only the priced portion — the NULL input price contributes nothing");
    }

    [Fact]
    public void CostDerivation_RoundsToSixDecimalPlaces()
    {
        var entry = Entry(inputPrice: 0.005m);

        var cost = _service.DeriveCost(new TokenCounts(1, 0), entry, providerCostHeaders: null, null);

        cost.Should().Be(0.000005m, "six decimal places is the project's cost-storage policy for cost_usd");
    }

    [Fact]
    public void CostDerivation_RoundsMidpointAwayFromZero()
    {
        var entry = Entry(inputPrice: 0.0045m);

        var cost = _service.DeriveCost(new TokenCounts(1, 0), entry, providerCostHeaders: null, null);

        cost.Should().Be(0.000005m, "0.0000045 rounds at the 6th decimal away from zero, not to-even");
    }

    [Fact]
    public void CostDerivation_NegativeTokens_TreatedAsZero()
    {
        var entry = Entry(inputPrice: 0.005m, outputPrice: 0.015m);

        var cost = _service.DeriveCost(new TokenCounts(-100, 50), entry, providerCostHeaders: null, null);

        cost.Should().Be(0.00075m, "a negative prompt count cannot produce a negative cost — clamped to 0");
    }

    [Fact]
    public void CostDerivation_ZeroTokens_PricesSet_ReturnsZero()
    {
        var entry = Entry(inputPrice: 0.005m, outputPrice: 0.015m);

        var cost = _service.DeriveCost(new TokenCounts(0, 0), entry, providerCostHeaders: null, null);

        cost.Should().Be(0m, "prices are known and nothing was consumed — 0 is a real answer here, unlike rule 3");
    }

    // ---- Rule 3: no provider cost and no prices → NULL --------------------

    [Fact]
    public void CostDerivation_PricesNotSet_NoProviderCost_ReturnsNull()
    {
        var entry = Entry();

        var cost = _service.DeriveCost(new TokenCounts(100, 50), entry, providerCostHeaders: null, null);

        cost.Should().BeNull("missing prices with no provider cost mean 'cost unknown' — never 0");
    }

    [Fact]
    public void CostDerivation_ModelCatalogEntryNull_FallsThroughToProviderAndNull()
    {
        var usage = Usage($$"""{"cost":{{ProviderCost}}}""");

        var withProviderCost = _service.DeriveCost(new TokenCounts(100, 50), null, providerCostHeaders: null, usage);
        var withoutProviderCost = _service.DeriveCost(new TokenCounts(100, 50), null, providerCostHeaders: null, null);

        withProviderCost.Should().Be(ProviderCost, "no catalog row must not block the provider-reported cost");
        withoutProviderCost.Should().BeNull("no catalog row and no provider cost → cost unknown");
    }

    // ---- Usage absent: unknown tokens cannot be priced --------------------

    [Fact]
    public void CostDerivation_NoUsageReported_NoProviderCost_ReturnsNull()
    {
        // Prices ARE set — but the provider reported no usage at all, so the
        // token counts are unknown and nothing can be priced. M3T1's mapper
        // doc promises cost_usd = NULL here, not a fabricated 0.
        var entry = Entry(inputPrice: 0.005m, outputPrice: 0.015m);

        var cost = _service.DeriveCost(new TokenCounts(null, null), entry, providerCostHeaders: null, null);

        cost.Should().BeNull("unknown tokens with no provider cost → cost unknown, never 0");
    }

    [Fact]
    public void CostDerivation_NoUsageReported_HeaderStillWins()
    {
        var cost = _service.DeriveCost(
            new TokenCounts(null, null), Entry(inputPrice: 0.005m), CostHeader, providerUsageBlock: null);

        cost.Should().Be(ProviderCost, "a provider-reported header cost needs no token counts");
    }

    [Fact]
    public void CostDerivation_NoUsageReported_UsageCostStillWins()
    {
        var usage = Usage($$"""{"cost":{{ProviderCost}}}""");

        var cost = _service.DeriveCost(
            new TokenCounts(null, null), Entry(inputPrice: 0.005m), providerCostHeaders: null, usage);

        cost.Should().Be(ProviderCost, "a provider-reported usage cost needs no token counts");
    }

    [Fact]
    public void CostDerivation_TotalOnlyReported_PricesSet_ReturnsNull()
    {
        // {"usage":{"total_tokens":42}} — HasUsage is true, but neither
        // direction is known; pricing prompt=0/completion=0 would fabricate $0.
        var entry = Entry(inputPrice: 0.005m, outputPrice: 0.015m);

        var cost = _service.DeriveCost(new TokenCounts(null, null, 42), entry, providerCostHeaders: null, null);

        cost.Should().BeNull("a total without the prompt/completion split cannot be priced");
    }

    [Fact]
    public void CostDerivation_CompletionOnlyReported_BothPricesSet_ReturnsNull()
    {
        // The input term's count is unknown while its price is set —
        // skipping the term would silently understate the cost.
        var entry = Entry(inputPrice: 0.005m, outputPrice: 0.015m);

        var cost = _service.DeriveCost(new TokenCounts(null, 10), entry, providerCostHeaders: null, null);

        cost.Should().BeNull("a set input price with an unreported prompt count → cost unknown, never partial");
    }

    [Fact]
    public void CostDerivation_CompletionOnlyReported_OutputPriceOnly_DerivesOutputCost()
    {
        // Only the output price is set and the completion count IS reported —
        // the single priced direction is fully known, so it derives.
        var entry = Entry(outputPrice: 0.015m);

        var cost = _service.DeriveCost(new TokenCounts(null, 10), entry, providerCostHeaders: null, null);

        cost.Should().Be(0.00015m, "(10/1000)*0.015 — the unpriced unknown direction contributes nothing");
    }

    [Fact]
    public void CostDerivation_ZeroInputPrice_PromptUnreported_StillDerivesOutputCost()
    {
        // A zero input price makes the unknown prompt count irrelevant —
        // its term is provably 0, so the derivable output term must not be
        // discarded with it.
        var entry = Entry(inputPrice: 0m, outputPrice: 0.015m);

        var cost = _service.DeriveCost(new TokenCounts(null, 10), entry, providerCostHeaders: null, null);

        cost.Should().Be(0.00015m, "input is free (0/1k) regardless of tokens; (10/1000)*0.015 output term stands");
    }

    // ---- Degraded surfaces fall through, they never throw -----------------

    [Fact]
    public void CostDerivation_HeaderUnparseable_FallsThroughToUsage()
    {
        var badHeader = new Dictionary<string, string> { ["x-litellm-cost"] = "not-a-number" };
        var usage = Usage($$"""{"cost":{{ProviderCost}}}""");

        var cost = _service.DeriveCost(new TokenCounts(100, 50), Entry(), badHeader, usage);

        cost.Should().Be(ProviderCost, "an unparseable header is treated as absent — the next rule applies");
    }

    [Fact]
    public void CostDerivation_UsageWithoutCost_FallsThroughToPrices()
    {
        var entry = Entry(inputPrice: 0.005m, outputPrice: 0.015m);
        var usage = Usage("""{"prompt_tokens":100,"completion_tokens":50}""");

        var cost = _service.DeriveCost(new TokenCounts(100, 50), entry, providerCostHeaders: null, usage);

        cost.Should().Be(0.00125m, "a usage block that reports tokens but no cost is not a provider-reported cost");
    }
}
