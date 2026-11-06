using EmailAgent.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmailAgent.Infrastructure.Configurations;

/// <summary>
/// SQ-4 <c>plugin_signatures</c> mapping (M2T3): snake_case columns. The
/// (plugin_id, version) uniqueness follows M1T2's style: the model keeps its
/// unique index and the migration replaces the EF-generated
/// <c>CreateIndex(unique: true)</c> with a real
/// <c>ALTER TABLE ... ADD CONSTRAINT ux_plugin_signatures_plugin_id_version</c>
/// statement, whose backing index carries the same name.
/// </summary>
internal sealed class PluginSignatureConfiguration : IEntityTypeConfiguration<PluginSignature>
{
    public void Configure(EntityTypeBuilder<PluginSignature> builder)
    {
        builder.ToTable("plugin_signatures");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(s => s.PluginId).HasColumnName("plugin_id");
        builder.Property(s => s.Version).HasColumnName("version");
        builder.Property(s => s.AssemblySha256).HasColumnName("assembly_sha256");
        builder.Property(s => s.Signature).HasColumnName("signature");

        builder.Property(s => s.FirstSeenAt)
            .HasColumnName("first_seen_at")
            .HasColumnType("timestamptz");

        builder.Property(s => s.LastSeenAt)
            .HasColumnName("last_seen_at")
            .HasColumnType("timestamptz");

        builder.HasIndex(s => new { s.PluginId, s.Version })
            .IsUnique()
            .HasDatabaseName("ux_plugin_signatures_plugin_id_version");
    }
}
