namespace EmailAgent.Infrastructure.Actions;

/// <summary>
/// M4T1 (SQ-M4-7 / Q-O18): bound from configuration section <c>Actions</c>
/// (env <c>ACTIONS__PendingConfirmationExpiry</c>).
/// </summary>
public sealed class ActionsOptions
{
    public const string SectionName = "Actions";

    /// <summary>
    /// Unapproved <c>pending_actions</c> requests lapse at
    /// <c>requested_at + PendingConfirmationExpiry</c>. Default 7 days (§12).
    /// </summary>
    public TimeSpan PendingConfirmationExpiry { get; set; } = TimeSpan.FromDays(7);
}
