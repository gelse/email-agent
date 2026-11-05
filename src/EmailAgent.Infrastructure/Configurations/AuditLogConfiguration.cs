using EmailAgent.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmailAgent.Infrastructure.Configurations;

/// <summary>§6 <c>audit_log</c> mapping (M1T2 task step 6).</summary>
internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_log");

        // bigserial PK (task step 6): generated on add by the store.
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(a => a.ActorUserId).HasColumnName("actor_user_id");
        builder.Property(a => a.ActorRole).HasColumnName("actor_role");

        builder.Property(a => a.Action)
            .HasColumnName("action")
            .IsRequired();

        builder.Property(a => a.TargetType).HasColumnName("target_type");
        builder.Property(a => a.TargetId).HasColumnName("target_id");

        builder.Property(a => a.MetadataJson)
            .HasColumnName("metadata_json")
            .HasColumnType("jsonb");

        builder.Property(a => a.Ip)
            .HasColumnName("ip")
            .HasColumnType("inet");

        builder.Property(a => a.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        // HMAC-SHA-256, 32 bytes (SQ-8 / Q-O12) — NOT NULL.
        builder.Property(a => a.Hmac)
            .HasColumnName("hmac")
            .HasColumnType("bytea")
            .IsRequired();

        // Query patterns (task scope): recency, actor recency, action lookups.
        builder.HasIndex(a => a.CreatedAt)
            .IsDescending(true)
            .HasDatabaseName("ix_audit_log_created_at");

        builder.HasIndex(a => new { a.ActorUserId, a.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_audit_log_actor_created_at");

        builder.HasIndex(a => a.Action)
            .HasDatabaseName("ix_audit_log_action");

        // Q-LP2: audit rows survive account deletion by design — the FK is
        // Restrict today (the users-row delete fails) and M6aT7's migration
        // drops it entirely to a soft reference.
        builder.HasOne(a => a.Actor)
            .WithMany(u => u.AuditLogEntries)
            .HasForeignKey(a => a.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}