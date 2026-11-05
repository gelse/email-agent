using System.Text.Json.Serialization;

namespace EmailAgent.Contracts.ActionHandlers;

/// <summary>
/// One externally visible effect an action performed or would have performed
/// (§10 step 6; M2 SQ-1) — JSON <c>{"kind":"sent_email","target":"...","detail":"..."}</c>.
/// The kind vocabulary (<c>SideEffectKinds</c>) is added by M4T1; M2T1 keeps
/// the shape only.
/// </summary>
public record SideEffect(
    [property: JsonPropertyName("kind")]
    [property: JsonRequired]
    string Kind,
    [property: JsonPropertyName("target")] string? Target,
    [property: JsonPropertyName("detail")] string? Detail);
