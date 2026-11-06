using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmailAgent.Infrastructure.Configurations;

/// <summary>§6 <c>pending_actions</c> mapping (M1T2 task steps 3 and 9).</summary>
internal sealed class PendingActionConfiguration : IEntityTypeConfiguration<PendingAction>
{
    public void Configure(EntityTypeBuilder<PendingAction> builder)
    {
        builder.ToTable("pending_actions");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(p => p.ProcessedMessageId).HasColumnName("processed_message_id");
        builder.Property(p => p.ActionConfigId).HasColumnName("action_config_id");

        builder.Property(p => p.ArgsJson)
            .HasColumnName("args_json")
            .HasColumnType("jsonb");

        // §6 enum(Pending, Approved, Rejected, Executed, Expired) NOT NULL
        // DEFAULT 'Pending' — text + CHECK per task step 9.
        builder.Property(p => p.Status)
            .HasColumnName("status")
            .HasColumnType("text")
            .HasConversion<string>()
            .HasDefaultValue(PendingActionStatus.Pending);

        builder.Property(p => p.RequestedAt)
            .HasColumnName("requested_at")
            .HasColumnType("timestamptz");

        builder.Property(p => p.DecidedAt)
            .HasColumnName("decided_at")
            .HasColumnType("timestamptz");

        builder.Property(p => p.ExecutedAt)
            .HasColumnName("executed_at")
            .HasColumnType("timestamptz");

        builder.Property(p => p.DecidedBy).HasColumnName("decided_by");

        builder.Property(p => p.ExpiresAt)
            .HasColumnName("expires_at")
            .HasColumnType("timestamptz");

        builder.Property(p => p.ResultJson)
            .HasColumnName("result_json")
            .HasColumnType("jsonb");

        builder.Property(p => p.ContextJson)
            .HasColumnName("context_json")
            .HasColumnType("jsonb");

        builder.Property(p => p.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        // Partial index (task scope): ix_pending_actions_status_requested
        // (status, requested_at) WHERE status = 'Pending' — raw SQL in the
        // migration (M1T2 task step 11).

        builder.ToTable("pending_actions", t => t.HasCheckConstraint(
            "ck_pending_actions_status",
            "status IN ('Pending', 'Approved', 'Rejected', 'Executed', 'Expired')"));

        builder.HasOne(p => p.ProcessedMessage)
            .WithMany(m => m.PendingActions)
            .HasForeignKey(p => p.ProcessedMessageId)
            .OnDelete(DeleteBehavior.Cascade);

        // §6: deleting a referenced action_configs row is refused (RFC 7807
        // 409 /errors/action-in-use) — the FK backs the application guard.
        builder.HasOne(p => p.ActionConfig)
            .WithMany(a => a.PendingActions)
            .HasForeignKey(p => p.ActionConfigId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(p => p.DecidedByUser)
            .WithMany()
            .HasForeignKey(p => p.DecidedBy)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
