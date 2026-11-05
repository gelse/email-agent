using EmailAgent.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmailAgent.Infrastructure.Configurations;

/// <summary>§6 <c>refresh_tokens</c> mapping (M1 SQ-1 / Q-A6; task step 5).</summary>
internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(r => r.UserId).HasColumnName("user_id");
        builder.Property(r => r.FamilyId).HasColumnName("family_id");

        // SHA-256 of the opaque token — bytea(32) UNIQUE NOT NULL (SQ-1).
        builder.Property(r => r.TokenHash)
            .HasColumnName("token_hash")
            .HasColumnType("bytea");

        builder.Property(r => r.IssuedAt)
            .HasColumnName("issued_at")
            .HasColumnType("timestamptz");

        builder.Property(r => r.ExpiresAt)
            .HasColumnName("expires_at")
            .HasColumnType("timestamptz");

        builder.Property(r => r.UsedAt)
            .HasColumnName("used_at")
            .HasColumnType("timestamptz");

        builder.Property(r => r.RevokedAt)
            .HasColumnName("revoked_at")
            .HasColumnType("timestamptz");

        builder.Property(r => r.ReplacedById).HasColumnName("replaced_by_id");
        builder.Property(r => r.UserAgent).HasColumnName("user_agent");

        builder.Property(r => r.Ip)
            .HasColumnName("ip")
            .HasColumnType("inet");

        // Indexes per the SQ-1 DDL (names kept verbatim).
        builder.HasIndex(r => r.UserId).HasDatabaseName("ix_refresh_tokens_user");
        builder.HasIndex(r => r.FamilyId).HasDatabaseName("ix_refresh_tokens_family");

        // A true UNIQUE constraint (raw SQL in the migration; task steps 11–12
        // assert it via pg_constraint) — name kept verbatim from SQ-1.
        builder.HasIndex(r => r.TokenHash)
            .IsUnique()
            .HasDatabaseName("ux_refresh_tokens_hash");

        builder.HasOne(r => r.User)
            .WithMany(u => u.RefreshTokens)
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Self-FK: never cascade-delete the chain head (task step 5).
        builder.HasOne(r => r.ReplacedBy)
            .WithMany()
            .HasForeignKey(r => r.ReplacedById)
            .OnDelete(DeleteBehavior.Restrict);
    }
}