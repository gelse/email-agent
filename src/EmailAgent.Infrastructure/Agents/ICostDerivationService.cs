using System.Text.Json;
using EmailAgent.Infrastructure.Entities;

namespace EmailAgent.Infrastructure.Agents;

/// <summary>
/// M3T4 (§11 cost-capture rule) — turns token counts, the model catalog's
/// admin-entered prices and any provider-reported cost into a single
/// <c>cost_usd</c> value. Consumed by M3T6's test-completion response and by
/// M5's <c>processed_messages.cost_usd</c> write.
/// </summary>
public interface ICostDerivationService
{
    /// <summary>
    /// Applies the three-rule precedence, in order:
    /// 1. provider-reported cost (the <c>x-litellm-cost</c> header, else
    ///    <c>usage.cost</c>) wins;
    /// 2. otherwise derive from <see cref="ModelCatalogEntry.PricePer1kInputUsd"/>
    ///    / <see cref="ModelCatalogEntry.PricePer1kOutputUsd"/> (whichever is
    ///    set) over the token counts, rounded to 6 decimal places;
    /// 3. otherwise <c>null</c> ("cost unknown") — never <c>0</c>.
    /// <para>
    /// Rule 2 is skipped when <see cref="TokenCounts.HasUsage"/> is
    /// <c>false</c> (the provider reported no usage at all) and whenever a
    /// non-zero price is set for a direction whose count was not reported
    /// (total-only or partial usage): unknown tokens cannot be priced, so the
    /// result is <c>null</c> even when prices are set. A zero price is exempt
    /// (its term is provably 0 whatever the count). Rules 1a/1b still apply
    /// in every case.
    /// </para>
    /// </summary>
    /// <param name="tokens">Provider token counts for the response.</param>
    /// <param name="modelCatalogEntry">
    /// The <c>model_catalog</c> row supplying the prices; may be <c>null</c>
    /// (no catalog row — rules 1 and 3 still apply).
    /// </param>
    /// <param name="providerCostHeaders">
    /// Response headers carrying a provider-reported cost; may be <c>null</c>.
    /// </param>
    /// <param name="providerUsageBlock">
    /// The response's raw <c>usage</c> block (e.g. <c>AgentDone.ProviderUsage</c>);
    /// may be <c>null</c>.
    /// </param>
    decimal? DeriveCost(
        TokenCounts tokens,
        ModelCatalogEntry? modelCatalogEntry,
        IReadOnlyDictionary<string, string>? providerCostHeaders,
        JsonElement? providerUsageBlock);
}
