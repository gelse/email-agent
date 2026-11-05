using EmailAgent.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmailAgent.Infrastructure.Configurations;

/// <summary>§6 <c>encrypted_blobs</c> mapping (M1T2 task step 3).</summary>
internal sealed class EncryptedBlobConfiguration : IEntityTypeConfiguration<EncryptedBlob>
{
    public void Configure(EntityTypeBuilder<EncryptedBlob> builder)
    {
        builder.ToTable("encrypted_blobs");

        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        // Soft reference: §6/Q-API47 delete a user by orphaning their blobs
        // (the DEK is destroyed) — no FK is mapped, and `owner_user_id` has no
        // navigation property on purpose (a navigation would make EF create
        // the FK by convention).
        builder.Property(b => b.OwnerUserId).HasColumnName("owner_user_id");

        builder.Property(b => b.Ciphertext)
            .HasColumnName("ciphertext")
            .HasColumnType("bytea");

        builder.Property(b => b.Nonce)
            .HasColumnName("nonce")
            .HasColumnType("bytea");

        builder.Property(b => b.Purpose).HasColumnName("purpose");

        builder.Property(b => b.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        builder.Property(b => b.RotatedAt)
            .HasColumnName("rotated_at")
            .HasColumnType("timestamptz");
    }
}