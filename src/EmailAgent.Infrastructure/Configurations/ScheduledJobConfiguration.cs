using EmailAgent.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmailAgent.Infrastructure.Configurations;

/// <summary>§6 <c>scheduled_jobs</c> mapping (M1T2 task steps 3 and 9).</summary>
internal sealed class ScheduledJobConfiguration : IEntityTypeConfiguration<ScheduledJob>
{
    public void Configure(EntityTypeBuilder<ScheduledJob> builder)
    {
        builder.ToTable("scheduled_jobs");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(s => s.SourceId).HasColumnName("source_id");

        builder.Property(s => s.ScheduledFor)
            .HasColumnName("scheduled_for")
            .HasColumnType("timestamptz");

        builder.Property(s => s.StartedAt)
            .HasColumnName("started_at")
            .HasColumnType("timestamptz");

        builder.Property(s => s.FinishedAt)
            .HasColumnName("finished_at")
            .HasColumnType("timestamptz");

        // §6 enum(Pending, Running, Succeeded, Failed, DeadLettered) — text +
        // CHECK per task step 9.
        builder.Property(s => s.Status)
            .HasColumnName("status")
            .HasColumnType("text")
            .HasConversion<string>();

        builder.Property(s => s.Error).HasColumnName("error");

        builder.Property(s => s.Attempt)
            .HasColumnName("attempt")
            .HasDefaultValue(0);

        builder.Property(s => s.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        // §6 enum(...) mirrored as CHECK (task step 9). The companion partial
        // index ix_scheduled_jobs_status_scheduled_for
        // (status, scheduled_for) WHERE status = 'Pending' is emitted as raw
        // SQL in the migration — M1T2 task step 11.
        builder.ToTable("scheduled_jobs", t => t.HasCheckConstraint(
            "ck_scheduled_jobs_status",
            "status IN ('Pending', 'Running', 'Succeeded', 'Failed', 'DeadLettered')"));

        builder.HasOne(s => s.Source)
            .WithMany(m => m.ScheduledJobs)
            .HasForeignKey(s => s.SourceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}