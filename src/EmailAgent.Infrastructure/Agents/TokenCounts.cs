using EmailAgent.Contracts.AgentEngines;

namespace EmailAgent.Infrastructure.Agents;

/// <summary>
/// M3T4 — token counts for one engine response, built via
/// <see cref="From(AgentDone?)"/> from M3T1's <c>AgentDone</c> event.
/// <para>
/// Counts are nullable on purpose: they preserve "the provider never
/// reported usage" (<see cref="HasUsage"/>), which the cost helper turns into
/// <c>cost_usd = NULL</c>. Coalescing absent counts to 0 at a call site would
/// instead fabricate "0 tokens, $0" — exactly what M3T1's
/// <c>OpenAIResponseMapper</c> doc comment promises will NOT happen.
/// </para>
/// </summary>
/// <param name="PromptTokens">Input tokens; <c>null</c> = not reported.</param>
/// <param name="CompletionTokens">Output tokens; <c>null</c> = not reported.</param>
/// <param name="TotalTokens">
/// Provider-reported total; <c>null</c> = not reported (the effective total
/// then falls back to the sum of the counts).
/// </param>
public record TokenCounts(int? PromptTokens, int? CompletionTokens, int? TotalTokens = null)
{
    /// <summary>
    /// <c>true</c> when the provider reported at least one token count.
    /// All-null means "usage absent": the cost helper then returns <c>null</c>
    /// after the provider cost surfaces (header / <c>usage.cost</c>). Note
    /// this is only the coarse signal — price derivation additionally
    /// requires every count whose direction has a set price
    /// (total-only / partial usage also yields <c>null</c> there).
    /// </summary>
    public bool HasUsage =>
        PromptTokens is not null || CompletionTokens is not null || TotalTokens is not null;

    /// <summary>
    /// The reported total, else the sum of the counts. Negative provider
    /// counts are treated as zero — the same policy <see cref="CostDerivationService"/>
    /// applies to pricing, so one bogus response cannot yield a non-negative
    /// cost next to a negative <c>total_tokens</c>.
    /// </summary>
    public int EffectiveTotalTokens => TotalTokens is { } total
        ? Math.Max(0, total)
        : Math.Max(0, PromptTokens ?? 0) + Math.Max(0, CompletionTokens ?? 0);

    /// <summary>
    /// Carries an engine's terminal event over verbatim — including its
    /// nulls. Never coalesce here: <c>null</c> means "not reported" and is
    /// the signal <see cref="HasUsage"/> exists to protect.
    /// <para>
    /// Carries only the counts — <c>done.ProviderUsage</c> (the
    /// <c>usage.cost</c> surface) must still be passed to
    /// <see cref="CostDerivationService.DeriveCost"/> separately.
    /// </para>
    /// </summary>
    public static TokenCounts From(AgentDone? done) =>
        new(done?.PromptTokens, done?.CompletionTokens, done?.TotalTokens);
}
