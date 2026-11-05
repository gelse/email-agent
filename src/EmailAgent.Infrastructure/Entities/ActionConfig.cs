namespace EmailAgent.Infrastructure.Entities;

/// <summary>
/// §6 <c>action_configs</c> row: per-action-handler configuration (Q-C4 —
/// SMTP credentials for <c>imap-reply</c>/<c>imap-forward</c> live in the
/// encrypted blob, entered manually by the user).
/// </summary>
public class ActionConfig
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>e.g. <c>imap-reply</c>, <c>telegram-send</c>.</summary>
    public string TypeId { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    /// <summary>DEK-encrypted per-action-handler configuration (Q-C4).</summary>
    public Guid ConfigBlobId { get; set; }

    /// <summary>§6 default <c>true</c>.</summary>
    public bool RequireConfirmation { get; set; } = true;

    /// <summary>§6 default <c>false</c>.</summary>
    public bool DryRunDefault { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public User User { get; set; } = null!;

    public EncryptedBlob ConfigBlob { get; set; } = null!;

    public ICollection<PendingAction> PendingActions { get; set; } = new List<PendingAction>();
}