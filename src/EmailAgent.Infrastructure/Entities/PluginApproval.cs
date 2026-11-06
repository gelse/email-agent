namespace EmailAgent.Infrastructure.Entities;

/// <summary>
/// SQ-4 <c>plugin_approvals</c> row (M2T3): an admin's approval of one
/// plugin version (Q-O5). Composite primary key (plugin_id, version) —
/// approving a new version inserts a new row (the admin UI lands in M7b;
/// until then rows are inserted by operators/tests). Fail-closed: the loader
/// refuses any candidate without a row, unless it matches
/// <c>DefaultTrustAnchor</c>.
/// </summary>
public sealed class PluginApproval
{
    /// <summary>Manifest <c>plugin_id</c> (part of the composite key).</summary>
    public string PluginId { get; set; } = null!;

    /// <summary>Manifest <c>version</c> (part of the composite key).</summary>
    public string Version { get; set; } = null!;

    /// <summary>The user (admin) who approved this version.</summary>
    public Guid ApprovedByUserId { get; set; }

    /// <summary>Navigation to <see cref="ApprovedByUserId"/>'s user.</summary>
    public User? ApprovedBy { get; set; }

    /// <summary>When the approval was granted.</summary>
    public DateTimeOffset ApprovedAt { get; set; }
}
