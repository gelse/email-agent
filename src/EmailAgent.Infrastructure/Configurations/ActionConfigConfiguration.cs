using EmailAgent.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmailAgent.Infrastructure.Configurations;

/// <summary>§6 <c>action_configs</c> mapping (M1T2 task step 3).</summary>
internal sealed class ActionConfigConfiguration : IEntityTypeConfiguration<ActionConfig>
{
    public void Configure(EntityTypeBuilder<ActionConfig> builder)
    {
        builder.ToTable("action_configs");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(a => a.UserId).HasColumnName("user_id");
        builder.Property(a => a.TypeId).HasColumnName("type_id");
        builder.Property(a => a.DisplayName).HasColumnName("display_name");
        builder.Property(a => a.ConfigBlobId).HasColumnName("config_blob_id");

        builder.Property(a => a.RequireConfirmation)
            .HasColumnName("require_confirmation")
            .HasDefaultValue(true);
        builder.Property(a => a.RequireConfirmation).Metadata.Sentinel = true;

        builder.Property(a => a.DryRunDefault)
            .HasColumnName("dry_run_default")
            .HasDefaultValue(false);

        builder.Property(a => a.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        builder.HasOne(a => a.User)
            .WithMany(u => u.ActionConfigs)
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(a => a.ConfigBlob)
            .WithMany()
            .HasForeignKey(a => a.ConfigBlobId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
