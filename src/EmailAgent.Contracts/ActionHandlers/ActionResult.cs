using System.Text.Json.Serialization;

namespace EmailAgent.Contracts.ActionHandlers;

/// <summary>
/// The outcome of <see cref="IActionHandler.ExecuteAsync"/> (§9 / §10 step 6;
/// M2 SQ-1) — wire form <c>{"ok":true,"error":null,"side_effects":[...]}.
/// </summary>
public record ActionResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] string? Error = null,
    [property: JsonPropertyName("side_effects")] IReadOnlyList<SideEffect>? SideEffects = null);
