using System.Text.Json.Serialization;

namespace EmailAgent.Contracts.ActionHandlers;

/// <summary>
/// Per-execution call context handed to
/// <see cref="IActionHandler.ExecuteAsync(JsonElement, JsonElement, ActionContext, CancellationToken)"/>
/// (§9; M2T1 step 8) so the action knows the user and message it acts on.
/// Additive properties (<c>DryRun</c>, <c>SourceMessage</c>) arrive in M4 —
/// see §9 "Agent event and context shapes".
/// </summary>
public record ActionContext(
    [property: JsonPropertyName("user_id")]
    [property: JsonRequired]
    Guid UserId,
    [property: JsonPropertyName("processed_message_id")]
    [property: JsonRequired]
    Guid ProcessedMessageId,
    [property: JsonIgnore] CancellationToken Ct);
