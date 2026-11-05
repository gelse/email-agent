using System.Text.Json.Serialization;
using EmailAgent.Contracts.MessageSources;

namespace EmailAgent.Contracts.AgentEngines;

/// <summary>
/// The input to <see cref="IAgentEngine.CompleteAsync(AgentRequest, CancellationToken)"/>
/// (§9; M2 SQ-1): the source message, the assembled prompt, the action
/// schemas the model may call (the engine maps each to its tool/function
/// format in M3), and the call context.
/// </summary>
/// <remarks>
/// Every member is <c>[JsonRequired]</c>: partial input fails loudly instead
/// of materialising <see langword="null"/> into non-nullable members. Record
/// equality over the collections is by reference — compare fields rather than
/// using <c>==</c>.
/// </remarks>
public record AgentRequest(
    [property: JsonRequired, JsonPropertyName("message")] RemoteMessage Message,
    [property: JsonRequired, JsonPropertyName("prompt")] string Prompt,
    [property: JsonRequired, JsonPropertyName("allowed_action_schemas")] IReadOnlyDictionary<string, JsonSchema> AllowedActionSchemas,
    [property: JsonRequired, JsonPropertyName("context")] AgentContext Context);
