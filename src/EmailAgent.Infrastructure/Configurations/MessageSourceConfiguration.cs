using EmailAgent.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmailAgent.Infrastructure.Configurations;

/// <summary>§6 <c>message_sources</c> mapping (M1T2 task steps 3 and 9).</summary>
internal sealed class MessageSourceConfiguration : IEntityTypeConfiguration<MessageSource>
{
    public void Configure(EntityTypeBuilder<MessageSource> builder)
    {
        builder.ToTable("message_sources");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(m => m.UserId).HasColumnName("user_id");
        builder.Property(m => m.TypeId).HasColumnName("type_id");
        builder.Property(m => m.DisplayName).HasColumnName("display_name");

        builder.Property(m => m.ConfigBlobId).HasColumnName("config_blob_id");

        builder.Property(m => m.IntervalMinutes)
            .HasColumnName("interval_minutes")
            .HasDefaultValue(5);

        builder.Property(m => m.CheckpointJson)
            .HasColumnName("checkpoint_json")
            .HasColumnType("jsonb")
            .HasDefaultValue("{}");

        // §6 enum(Active, Paused, Error) — text + CHECK per task step 9.
        builder.Property(m => m.Status)
            .HasColumnName("status")
            .HasColumnType("text")
            .HasConversion<string>();

        builder.Property(m => m.LastRunAt)
            .HasColumnName("last_run_at")
            .HasColumnType("timestamptz");

        builder.Property(m => m.NextRunAt)
            .HasColumnName("next_run_at")
            .HasColumnType("timestamptz");

        builder.Property(m => m.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        builder.ToTable("message_sources", t => t.HasCheckConstraint(
            "ck_message_sources_status",
            "status IN ('Active', 'Paused', 'Error')"));

        builder.HasOne(m => m.User)
            .WithMany(u => u.MessageSources)
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Q-O15: the config blob is deleted source-driven by application code
        // in the same transaction — never cascade blob-deletion into the
        // source row.
        builder.HasOne(m => m.ConfigBlob)
            .WithMany()
            .HasForeignKey(m => m.ConfigBlobId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}