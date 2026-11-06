namespace EmailAgent.Connectors.OpenAI;

/// <summary>
/// Plugin-local call options for the OpenAI-compatible engine (M3T1 step 11):
/// the streaming toggle the <c>IAgentEngine</c> interface itself does not carry,
/// and the per-call deadline bounding the whole engine call.
/// </summary>
/// <remarks>
/// This type is deliberately NOT part of <c>EmailAgent.Contracts</c> and NOT a
/// member of <see cref="EmailAgent.Contracts.AgentEngines.IAgentEngine"/> —
/// the M2T1 connector contract stays verbatim (AGENTS.md hard constraint #8).
/// The interface method delegates with <see cref="Default"/> (<c>Stream=false</c>,
/// the probe / test-completion path); in-process callers that want SSE (this
/// task's streaming tests, M5's live-log through an Api-layer adapter) resolve
/// the concrete type and use the additive
/// <see cref="OpenAICompatibleAgentEngine"/> overload.
/// </remarks>
/// <param name="Stream">
/// Whether to consume the SSE streaming shape (<see langword="true"/>) or the
/// single buffered JSON body (<see langword="false"/>).
/// </param>
/// <param name="Timeout">
/// The per-call budget applied to the WHOLE engine call — send, headers and
/// body alike. <see langword="null"/> uses the engine's default (30 minutes).
/// Interactive callers (M3T2's probe, M3T6's admin test-completion) should
/// pass a short value; background generations can keep the default.
/// </param>
public sealed record EngineCallOptions(bool Stream = false, TimeSpan? Timeout = null)
{
    /// <summary>The default call shape: a single non-streamed JSON response.</summary>
    public static readonly EngineCallOptions Default = new(Stream: false);
}
