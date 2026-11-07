using System.Text.Json;
using EmailAgent.Infrastructure.Entities;

namespace EmailAgent.Infrastructure.Agents;

/// <summary>
/// M3T4 (§11) — pure, thread-safe implementation of the cost-capture rule.
/// No I/O: no database, no network; every input is passed in by the caller
/// (M3T6's test-completion endpoint, M5's per-message pipeline).
/// </summary>
public sealed class CostDerivationService : ICostDerivationService
{
    /// <summary>The LiteLLM cost header; any name may be passed to the extractor instead.</summary>
    public const string LiteLlmCostHeaderName = "x-litellm-cost";

    public decimal? DeriveCost(
        TokenCounts tokens,
        ModelCatalogEntry? modelCatalogEntry,
        IReadOnlyDictionary<string, string>? providerCostHeaders,
        JsonElement? providerUsageBlock)
    {
        // Rule 1a: the header beats everything (explicit precedence over
        // usage.cost — asserted by CostDerivation_HeaderTakesPrecedenceOverUsage).
        var headerCost = ProviderCostExtractor.ExtractFromHeader(providerCostHeaders, LiteLlmCostHeaderName);
        if (headerCost is not null)
        {
            return Round(headerCost.Value);
        }

        // Rule 1b: provider usage block's own cost field.
        var usageCost = ProviderCostExtractor.ExtractFromUsage(providerUsageBlock);
        if (usageCost is not null)
        {
            return Round(usageCost.Value);
        }

        // Rule 2: no provider cost — derive from admin prices. First, usage
        // absent entirely (all counts null): tokens UNKNOWN → cost unknown.
        // This is the fallback M3T1's OpenAIResponseMapper doc promises
        // (cost_usd = NULL); collapsing absent counts to 0 at the call site
        // would fabricate $0 instead. Rules 1a/1b above still applied.
        if (!tokens.HasUsage)
        {
            return null;
        }

        if (modelCatalogEntry is null)
        {
            return null;
        }

        var inPrice = modelCatalogEntry.PricePer1kInputUsd;
        var outPrice = modelCatalogEntry.PricePer1kOutputUsd;

        // Rule 3: neither price set → cost unknown → NULL (never 0).
        if (inPrice is null && outPrice is null)
        {
            return null;
        }

        // A SET, non-zero price whose count the provider did not report
        // cannot be priced honestly — skipping the term would silently
        // understate (total-only usage, or a single-direction usage block).
        // Cost is unknown → NULL, never a fabricated partial/zero cost.
        // A zero price is exempt (review suggestion): its term is provably 0
        // whatever the count, so an unknown count there changes nothing.
        var inputNeedsCount = inPrice is { } inputPrice && inputPrice != 0m;
        if (inputNeedsCount && tokens.PromptTokens is null)
        {
            return null;
        }

        var outputNeedsCount = outPrice is { } outputPriceValue && outputPriceValue != 0m;
        if (outputNeedsCount && tokens.CompletionTokens is null)
        {
            return null;
        }

        var promptTokens = Math.Max(0, tokens.PromptTokens ?? 0);
        var completionTokens = Math.Max(0, tokens.CompletionTokens ?? 0);
        decimal cost = 0m;
        if (inPrice is { } pricedInput)
        {
            cost += (promptTokens / 1000m) * pricedInput;
        }
        if (outPrice is { } pricedOutput)
        {
            cost += (completionTokens / 1000m) * pricedOutput;
        }

        return Round(cost);
    }

    /// <summary>
    /// 6 decimal places, away from zero at the midpoint — the project's
    /// cost-storage policy for <c>cost_usd</c>. Note the deliberate
    /// consequence for provider-reported costs: a genuine cost below 5e-7 USD
    /// rounds to <c>0.000000</c>.
    /// </summary>
    private static decimal Round(decimal d) => Math.Round(d, 6, MidpointRounding.AwayFromZero);
}
