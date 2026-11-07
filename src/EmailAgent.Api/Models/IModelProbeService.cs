using EmailAgent.Connectors.OpenAI;

namespace EmailAgent.Api.Models;

/// <summary>
/// M3T2 — the tool-calling capabilities probe (Q-L4 / SQ-M3-3): a minimal
/// non-streaming chat-completions call through M3T1's <c>IAgentEngine</c>
/// interface asserting the model answers with a <c>ping</c> tool call. The
/// catalog endpoint (M3T3) runs it BEFORE any database write (SQ-M3-11);
/// <c>Pass</c> / <c>PassFromCache</c> permit the write, <c>Fails</c> is a
/// non-recoverable validation error.
/// </summary>
/// <remarks>
/// <b>Internal on purpose (M3T2 deviation D3):</b> <see cref="ProbeAsync"/>
/// takes M3T1's <c>internal</c> <c>OpenAICompatibleConfig</c>, and CS0051
/// forbids exposing that type through a public Api member — even across
/// <c>InternalsVisibleTo</c>. The sole in-assembly consumer is M3T3's catalog
/// endpoint; the test suites reach the seam via the friend declarations in
/// <c>EmailAgent.Api.csproj</c>.
/// </remarks>
internal interface IModelProbeService
{
    /// <summary>
    /// Probes the model behind <paramref name="config"/> for tool-calling
    /// capability.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Cache first:</b> the newest <c>model_catalog</c> row with the same
    /// <c>(base_url, model_name)</c> whose result is <c>Pass</c> and whose
    /// <c>tool_calling_probed_at</c> is within
    /// <c>ModelOptions.ProbeReprobeWindow</c> serves the call as
    /// <see cref="ProbeOutcome.PassFromCache"/> — no HTTP call, no audit row.
    /// The cache key is deliberately <c>(base_url, model_name)</c> ONLY: an
    /// <c>api_key</c> rotation does not change tool-calling capability and must
    /// not invalidate the cache, and <paramref name="correlationModelId"/> is
    /// audit-correlation only (it never participates in the key). A stored
    /// <c>Fail</c> never serves cache (only <c>Pass</c> rows are eligible).
    /// </para>
    /// <para>
    /// <b>Probe:</b> otherwise one non-streaming completion via
    /// <c>IAgentEngine.CompleteAsync</c> (the interface default — no direct
    /// HTTP client exists in M3T2), asserting the SQ-M3-3 pass condition: a
    /// non-empty <c>tool_calls</c> array whose entries are all named
    /// <c>ping</c>.
    /// </para>
    /// <para>
    /// <b>Audit:</b> on <see cref="ProbeOutcome.Fail"/> exactly one
    /// <c>ModelProbeFailed</c> row is emitted (Q-O17 — there is exactly one
    /// probe audit action in M3); pass outcomes leave the audit trail to
    /// M3T3's <c>CreateModel</c> / <c>UpdateModel</c> metadata. The row's
    /// metadata never contains the plaintext <c>api_key</c>. Engine transport
    /// errors (<c>EngineCallException</c>) propagate to the caller without an
    /// audit row — they are connection/auth failures, not a failed capability
    /// assertion (M3T2 task deviation, documented).
    /// </para>
    /// </remarks>
    /// <param name="config">The model endpoint, key and model name to probe.</param>
    /// <param name="correlationModelId">The catalog row id on the PATCH path; <c>null</c> on the POST path — no row exists yet, so <c>target_id</c> stays <c>NULL</c> (SQ-M3-11).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The typed probe result.</returns>
    Task<ProbeResult> ProbeAsync(OpenAICompatibleConfig config, Guid? correlationModelId, CancellationToken ct);
}
