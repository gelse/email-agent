using EmailAgent.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmailAgent.Infrastructure.Configurations;

/// <summary>§6 <c>user_notifications</c> mapping (M1T2 task step 3).</summary>
internal sealed class UserNotificationConfiguration : IEntityTypeConfiguration<UserNotification>
{
    public void Configure(EntityTypeBuilder<UserNotification> builder)
    {
        builder.ToTable("user_notifications");

        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(n => n.UserId).HasColumnName("user_id");
        builder.Property(n => n.Kind).HasColumnName("kind");
        builder.Property(n => n.Title).HasColumnName("title");
        builder.Property(n => n.Body).HasColumnName("body");
        builder.Property(n => n.RelatedType).HasColumnName("related_type");
        builder.Property(n => n.RelatedId).HasColumnName("related_id");

        builder.Property(n => n.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        builder.Property(n => n.ReadAt)
            .HasColumnName("read_at")
            .HasColumnType("timestamptz");

        builder.HasIndex(n => new { n.UserId, n.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_user_notifications_user_created");

        // Companion partial index ix_user_notifications_unread
        // (user_id, created_at DESC) WHERE read_at IS NULL is emitted as raw
        // SQL in the migration (M1T2 task step 11).

        builder.HasOne(n => n.User)
            .WithMany(u => u.UserNotifications)
            .HasForeignKey(n => n.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
