using System.Text.Json.Serialization;

namespace EmailAgent.Contracts.ActionHandlers;

/// <summary>
/// M4 (Q-CE6 / SQ-M4-4): snapshot of the originating message passed to
/// <see cref="ActionContext.SourceMessage"/>. Threading headers and the
/// reply recipient are derived from this snapshot (never from model args),
/// which keeps prompt-injected addressing out of the handler's control
/// surface. The dispatcher's caller (M5's worker pool in production, the
/// M4 test harness in-process) constructs it.
/// </summary>
public record SourceMessageSnapshot(
    [property: JsonPropertyName("remote_uid")]
    [property: JsonRequired]
    string RemoteUid,
    [property: JsonPropertyName("message_id")] string? MessageId,
    [property: JsonPropertyName("subject")] string? Subject,
    [property: JsonPropertyName("from")] string? From,
    [property: JsonPropertyName("reply_to")] string? ReplyTo,
    [property: JsonPropertyName("to")] string? To,
    [property: JsonPropertyName("references")] string? References,
    [property: JsonPropertyName("body_text")]
    [property: JsonRequired]
    string BodyText);
