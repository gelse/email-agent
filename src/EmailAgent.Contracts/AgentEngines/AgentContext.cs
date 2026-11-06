using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmailAgent.Contracts.AgentEngines;

/// <summary>
/// Per-completion call context handed to
/// <see cref="IAgentEngine.CompleteAsync(AgentRequest, CancellationToken)"/>
/// (§9; M2T1 step 11). Additive property <c>EngineConfigJson</c> (M3T1,
/// SQ-M3-10): the plugin's engine configuration document, populated by the
/// caller (M3T2 probe, M3T6 test-completion, M5 worker pool) from the
/// persisted model catalog before invoking the engine. Nullable so every
/// existing M2T1 construction site keeps compiling.
/// </summary>
public record AgentContext(
    [property: JsonPropertyName("user_id")]
    [property: JsonRequired]
    Guid UserId,
    [property: JsonPropertyName("source_id")]
    [property: JsonRequired]
    Guid SourceId,
    [property: JsonIgnore] CancellationToken Ct)
{
    /// <summary>
    /// Engine configuration JSON for the call (<c>base_url</c>, <c>api_key</c>,
    /// <c>model_name</c> for the OpenAI-compatible engine), or
    /// <see langword="null"/> when the caller did not supply one. Engines that
    /// need configuration fail the call with
    /// <see cref="InvalidOperationException"/> when it is absent (M3T1 SQ-M3-10).
    /// In-process only: excluded from the request wire — a caller binding an
    /// <c>AgentRequest</c> body must not be able to inject an endpoint and key
    /// into an engine call (M3T1 review fix, §14 hardening note).
    /// </summary>
    [JsonIgnore]
    public JsonElement? EngineConfigJson { get; init; }

    /// <summary>
    /// Identity-only rendering: <see cref="EngineConfigJson"/> prints as
    /// <c>null</c>/<c>***</c>, never verbatim — it carries the decrypted
    /// <c>api_key</c>, which must never reach a log line, an exception message
    /// or an assertion failure (the M3T1 <c>OpenAICompatibleConfig.ToString</c>
    /// redaction precedent applied to the context that transports the same
    /// credential).
    /// </summary>
    public override string ToString() =>
        $"AgentContext {{ UserId = {UserId}, SourceId = {SourceId}, EngineConfigJson = "
        + (EngineConfigJson is null ? "null" : "***") + " } }";
}
