using EmailAgent.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmailAgent.Infrastructure.Configurations;

/// <summary>
/// SQ-4 <c>plugin_approvals</c> mapping (M2T3): composite primary key
/// (plugin_id, version), FK to <c>users.id</c> named by EF's default
/// (<c>FK_plugin_approvals_users_approved_by_user_id</c>).
/// </summary>
internal sealed class PluginApprovalConfiguration : IEntityTypeConfiguration<PluginApproval>
{
    public void Configure(EntityTypeBuilder<PluginApproval> builder)
    {
        builder.ToTable("plugin_approvals");

        builder.HasKey(a => new { a.PluginId, a.Version });

        builder.Property(a => a.PluginId).HasColumnName("plugin_id");
        builder.Property(a => a.Version).HasColumnName("version");
        builder.Property(a => a.ApprovedByUserId).HasColumnName("approved_by_user_id");

        builder.Property(a => a.ApprovedAt)
            .HasColumnName("approved_at")
            .HasColumnType("timestamptz");

        // Approvals die with the approving user (no orphaned self-approval
        // claims) — cascade is EF's default for a required FK and matches the
        // task's CreateTable snippet (no onDelete clause).
        builder.HasOne(a => a.ApprovedBy)
            .WithMany()
            .HasForeignKey(a => a.ApprovedByUserId);
    }
}
