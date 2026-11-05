using EmailAgent.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmailAgent.Infrastructure.Configurations;

/// <summary>§6 <c>agent_configs</c> mapping (M1T2 task steps 3 and 9).</summary>
internal sealed class AgentConfigConfiguration : IEntityTypeConfiguration<AgentConfig>
{
    public void Configure(EntityTypeBuilder<AgentConfig> builder)
    {
        builder.ToTable("agent_configs");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(a => a.UserId).HasColumnName("user_id");
        builder.Property(a => a.SourceId).HasColumnName("source_id");
        builder.Property(a => a.ModelId).HasColumnName("model_id");

        builder.Property(a => a.PromptText).HasColumnName("prompt_text");

        builder.Property(a => a.PromptBlobId).HasColumnName("prompt_blob_id");

        builder.Property(a => a.PrefilterJson)
            .HasColumnName("prefilter_json")
            .HasColumnType("jsonb")
            .HasDefaultValue("[]");

        builder.Property(a => a.PrefilterCombiner)
            .HasColumnName("prefilter_combiner")
            .HasDefaultValue("OR");

        builder.Property(a => a.AllowedActionTypeIds)
            .HasColumnName("allowed_action_type_ids")
            .HasColumnType("text[]")
            .HasDefaultValueSql("'{}'");

        builder.Property(a => a.AbortOnActionFailure)
            .HasColumnName("abort_on_action_failure")
            .HasDefaultValue(false);

        builder.Property(a => a.Enabled)
            .HasColumnName("enabled")
            .HasDefaultValue(true);
        builder.Property(a => a.Enabled).Metadata.Sentinel = true;

        // Ownership: user-scoped children, deleted with the user.
        builder.HasOne(a => a.User)
            .WithMany(u => u.AgentConfigs)
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // A source owns its configs: deleting a source removes them. (§6/Q-O15
        // retention applies to processed_messages only.)
        builder.HasOne(a => a.Source)
            .WithMany(s => s.AgentConfigs)
            .HasForeignKey(a => a.SourceId)
            .OnDelete(DeleteBehavior.Cascade);

        // The catalogue entry is not owned — deleting a referenced model is
        // refused rather than cascading into user configs.
        builder.HasOne(a => a.Model)
            .WithMany(m => m.AgentConfigs)
            .HasForeignKey(a => a.ModelId)
            .OnDelete(DeleteBehavior.Restrict);

        // Blob rows are deleted by application code after the reference moves;
        // never cascade a blob delete into the config.
        builder.HasOne(a => a.PromptBlob)
            .WithMany()
            .HasForeignKey(a => a.PromptBlobId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}