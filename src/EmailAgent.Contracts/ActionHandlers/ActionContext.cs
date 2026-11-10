using System.Text.Json.Serialization;

namespace EmailAgent.Contracts.ActionHandlers;

/// <summary>
/// Per-execution call context handed to
/// <see cref="IActionHandler.ExecuteAsync(JsonElement, JsonElement, ActionContext, CancellationToken)"/>
/// (§9; M2T1 step 8) so the action knows the user and message it acts on.
/// M4 (Q-CE6 / SQ-M4-4) adds <see cref="DryRun"/> and
/// <see cref="SourceMessage"/> as additive init-only properties — no
/// interface method changes (AGENTS.md #8).
/// </summary>
public record ActionContext(
    [property: JsonPropertyName("user_id")]
    [property: JsonRequired]
    Guid UserId,
    [property: JsonPropertyName("processed_message_id")]
    [property: JsonRequired]
    Guid ProcessedMessageId,
    [property: JsonIgnore] CancellationToken Ct)
{
    /// <summary>
    /// M4: when <c>true</c> the handler must describe the would-be side
    /// effect and MUST NOT execute it (no network session). Defaults to
    /// <c>false</c>. Omitted from JSON when <c>false</c> to preserve the M2
    /// wire shape.
    /// </summary>
    [JsonPropertyName("dry_run")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DryRun { get; init; }

    /// <summary>
    /// M4: snapshot of the originating message (threading headers, sender,
    /// body text). Required by <c>imap-reply</c>/<c>imap-forward</c>; a
    /// <c>null</c> snapshot is a handler <c>failed</c> decision, not
    /// invalid args (SQ-M4-4). Omitted when <c>null</c>.
    /// </summary>
    [JsonPropertyName("source_message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SourceMessageSnapshot? SourceMessage { get; init; }
}
