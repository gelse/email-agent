using System.Text.Json.Serialization;

namespace EmailAgent.Contracts.AgentEngines;

/// <summary>
/// Per-completion call context handed to
/// <see cref="IAgentEngine.CompleteAsync(AgentRequest, CancellationToken)"/>
/// (§9; M2T1 step 11). Additive property <c>EngineConfigJson</c> arrives in
/// M3 — see §9 "Agent event and context shapes" (Q-CE4).
/// </summary>
public record AgentContext(
    [property: JsonPropertyName("user_id")]
    [property: JsonRequired]
    Guid UserId,
    [property: JsonPropertyName("source_id")]
    [property: JsonRequired]
    Guid SourceId,
    [property: JsonIgnore] CancellationToken Ct);
