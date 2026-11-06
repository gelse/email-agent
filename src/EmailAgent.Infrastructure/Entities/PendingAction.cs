using EmailAgent.Infrastructure.Enums;

namespace EmailAgent.Infrastructure.Entities;

/// <summary>
/// §6 <c>pending_actions</c> row: restart-safe confirm-before-send queue
/// (Q-L6). The <c>context_json</c> snapshot is written at insert time so the
/// approve path can reconstruct the source-message context later (Q-AC9) —
/// never <c>body_text</c>, which stays only in the encrypted blob.
/// </summary>
public class PendingAction
{
    public Guid Id { get; set; }

    public Guid ProcessedMessageId { get; set; }

    public Guid ActionConfigId { get; set; }

    /// <summary>Validated model-supplied args (jsonb).</summary>
    public string ArgsJson { get; set; } = "{}";

    public PendingActionStatus Status { get; set; } = PendingActionStatus.Pending;

    public DateTimeOffset RequestedAt { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public DateTimeOffset? ExecutedAt { get; set; }

    /// <summary>Approver (soft actor reference); NULL while pending.</summary>
    public Guid? DecidedBy { get; set; }

    /// <summary>When an unapproved request lapses (default 7 days after <c>requested_at</c>).</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>ActionResult after execution (jsonb); NULL until executed.</summary>
    public string? ResultJson { get; set; }

    /// <summary>Context snapshot written at insert (jsonb, Q-AC9).</summary>
    public string? ContextJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public ProcessedMessage ProcessedMessage { get; set; } = null!;

    public ActionConfig ActionConfig { get; set; } = null!;

    public User? DecidedByUser { get; set; }
}
