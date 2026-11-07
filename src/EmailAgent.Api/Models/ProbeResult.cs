namespace EmailAgent.Api.Models;

/// <summary>Outcome of the M3T2 tool-calling capabilities probe (SQ-M3-3).</summary>
public enum ProbeOutcome
{
    /// <summary>Served from a recent passing <c>model_catalog</c> row — no engine call.</summary>
    PassFromCache,

    /// <summary>The engine call satisfied the pass condition (non-empty <c>tool_calls</c>, all named <c>ping</c>).</summary>
    Pass,

    /// <summary>The engine call failed the pass condition.</summary>
    Fail,
}

/// <summary>
/// Result of <see cref="IModelProbeService.ProbeAsync"/> — typed outcome plus the
/// excerpts M3T3 surfaces (canonical request body, UTF-8-clamped response body).
/// The duration is deliberately NOT carried here: it is a property of the engine
/// call (a cache hit has duration 0) and is passed separately to the fail-only
/// audit emission.
/// </summary>
/// <remarks>
/// <see cref="CachedFrom"/> is non-null only when <see cref="Outcome"/> is
/// <see cref="ProbeOutcome.PassFromCache"/>; <see cref="Reason"/> is non-null
/// only on <see cref="ProbeOutcome.Fail"/> (<c>no_tool_calls</c> |
/// <c>unexpected_tool_names</c>). Excerpts are empty on a cache hit. Neither
/// excerpt ever contains the plaintext <c>api_key</c> — the key travels only in
/// the engine's <c>Authorization: Bearer</c> header, never in a serialized body.
/// </remarks>
public record ProbeResult(
    ProbeOutcome Outcome,
    string? Reason,
    string RequestExcerpt,
    string ResponseExcerpt,
    DateTimeOffset ProbedAt,
    DateTimeOffset? CachedFrom)
{
    /// <summary>A passing probe served from a within-window catalog row.</summary>
    /// <param name="cachedFrom">The catalog row's <c>tool_calling_probed_at</c> — the timestamp of the probe that is being honored.</param>
    /// <param name="probedAt">The current UTC time (no engine call ran now).</param>
    public static ProbeResult PassFromCacheResult(DateTimeOffset cachedFrom, DateTimeOffset probedAt) =>
        new(
            ProbeOutcome.PassFromCache,
            Reason: null,
            RequestExcerpt: string.Empty,
            ResponseExcerpt: string.Empty,
            ProbedAt: probedAt,
            CachedFrom: cachedFrom);

    /// <summary>A probe that ran now and satisfied the SQ-M3-3 pass condition.</summary>
    public static ProbeResult PassResult(string requestExcerpt, string responseExcerpt, DateTimeOffset probedAt) =>
        new(
            ProbeOutcome.Pass,
            Reason: null,
            RequestExcerpt: requestExcerpt,
            ResponseExcerpt: responseExcerpt,
            ProbedAt: probedAt,
            CachedFrom: null);

    /// <summary>A probe that ran now and failed the SQ-M3-3 pass condition.</summary>
    /// <param name="reason"><c>no_tool_calls</c> (empty array) or <c>unexpected_tool_names</c> (mixed names).</param>
    public static ProbeResult FailResult(
        string reason, string requestExcerpt, string responseExcerpt, DateTimeOffset probedAt) =>
        new(
            ProbeOutcome.Fail,
            Reason: reason,
            RequestExcerpt: requestExcerpt,
            ResponseExcerpt: responseExcerpt,
            ProbedAt: probedAt,
            CachedFrom: null);
}
