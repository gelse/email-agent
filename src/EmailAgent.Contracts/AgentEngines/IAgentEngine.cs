using System.Text.Json;

namespace EmailAgent.Contracts.AgentEngines;

/// <summary>
/// An agent engine (e.g. the OpenAI-compatible plugin) that turns an
/// <see cref="AgentRequest"/> into a stream of <see cref="AgentEvent"/>s
/// (§9 "Core interfaces"; M2T1 step 10 — interface only, the concrete
/// implementation lands in M3).
/// </summary>
public interface IAgentEngine
{
    /// <summary>The engine's stable type id, e.g. <c>"openai-compatible"</c>.</summary>
    string TypeId { get; }

    /// <summary>JSON Schema describing the engine configuration (base_url, api key ref, model).</summary>
    JsonSchema ConfigSchema { get; }

    /// <summary>Validates an engine configuration document against <see cref="ConfigSchema"/> and engine rules.</summary>
    Task<ValidationResult> ValidateConfigAsync(JsonElement config, CancellationToken ct);

    /// <summary>
    /// Runs a completion and streams success-path events. Errors are not
    /// events: the implementation throws a typed exception instead
    /// (§9 "Agent event and context shapes", Q-CE1).
    /// </summary>
    IAsyncEnumerable<AgentEvent> CompleteAsync(AgentRequest req, CancellationToken ct);
}
