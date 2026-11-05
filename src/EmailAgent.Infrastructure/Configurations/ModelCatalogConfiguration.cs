using EmailAgent.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmailAgent.Infrastructure.Configurations;

/// <summary>§6 <c>model_catalog</c> mapping (M1T2 task steps 3 and 9).</summary>
internal sealed class ModelCatalogConfiguration : IEntityTypeConfiguration<ModelCatalogEntry>
{
    public void Configure(EntityTypeBuilder<ModelCatalogEntry> builder)
    {
        builder.ToTable("model_catalog");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(m => m.DisplayName).HasColumnName("display_name");
        builder.Property(m => m.Provider).HasColumnName("provider");
        builder.Property(m => m.BaseUrl).HasColumnName("base_url");
        builder.Property(m => m.ModelName).HasColumnName("model_name");
        builder.Property(m => m.ApiKeySecretRef).HasColumnName("api_key_secret_ref");

        builder.Property(m => m.PricePer1kInputUsd).HasColumnName("price_per_1k_input_usd");
        builder.Property(m => m.PricePer1kOutputUsd).HasColumnName("price_per_1k_output_usd");

        builder.Property(m => m.ToolCallingProbedAt)
            .HasColumnName("tool_calling_probed_at")
            .HasColumnType("timestamptz");

        // §6 enum(Pass, Fail) NULL = never probed — text + CHECK per task step 9.
        builder.Property(m => m.ToolCallingProbeResult)
            .HasColumnName("tool_calling_probe_result")
            .HasColumnType("text")
            .HasConversion<string>();

        builder.Property(m => m.Enabled)
            .HasColumnName("enabled")
            .HasDefaultValue(true);
        builder.Property(m => m.Enabled).Metadata.Sentinel = true;

        builder.Property(m => m.CreatedBy).HasColumnName("created_by");

        builder.Property(m => m.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        builder.ToTable("model_catalog", t => t.HasCheckConstraint(
            "ck_model_catalog_tool_calling_probe_result",
            "tool_calling_probe_result IN ('Pass', 'Fail')"));

        // Actor reference (the creating admin): rows must survive account
        // erasure — Restrict, mirroring audit_log.actor_user_id (Q-LP2).
        builder.HasOne(m => m.CreatedByUser)
            .WithMany(u => u.CreatedModelCatalogEntries)
            .HasForeignKey(m => m.CreatedBy)
            .OnDelete(DeleteBehavior.Restrict);
    }
}