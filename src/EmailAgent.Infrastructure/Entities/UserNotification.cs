namespace EmailAgent.Infrastructure.Entities;

/// <summary>
/// §6 <c>user_notifications</c> row: in-app user notifications (Q-AC2).
/// </summary>
public class UserNotification
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>
    /// e.g. <c>action_invalid_args</c>, <c>action_failed</c>,
    /// <c>dead_letter</c>, <c>passphrase_changed_elsewhere</c>,
    /// <c>model_probe_failed</c>.
    /// </summary>
    public string Kind { get; set; } = null!;

    public string Title { get; set; } = null!;

    public string? Body { get; set; }

    /// <summary>e.g. <c>processed_message</c>, <c>action_config</c>, <c>model_catalog</c>.</summary>
    public string? RelatedType { get; set; }

    public Guid? RelatedId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ReadAt { get; set; }

    public User User { get; set; } = null!;
}