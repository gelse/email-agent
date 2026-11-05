using EmailAgent.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmailAgent.Infrastructure.Configurations;

/// <summary>§6 <c>one_time_passwords</c> mapping (M1T2 task step 7).</summary>
internal sealed class OneTimePasswordConfiguration : IEntityTypeConfiguration<OneTimePassword>
{
    public void Configure(EntityTypeBuilder<OneTimePassword> builder)
    {
        builder.ToTable("one_time_passwords");

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(o => o.UserId)
            .HasColumnName("user_id");

        // PBKDF2-SHA256 salt||hash (16 + 32 = 48 bytes) written by M1T5 —
        // Q-A8 / SQ-5.
        builder.Property(o => o.OtpHash)
            .HasColumnName("otp_hash")
            .HasColumnType("bytea");

        builder.Property(o => o.ExpiresAt)
            .HasColumnName("expires_at")
            .HasColumnType("timestamptz");

        builder.Property(o => o.UsedAt)
            .HasColumnName("used_at")
            .HasColumnType("timestamptz");

        // SQ-6 / Q-A9.
        builder.Property(o => o.RevokedAt)
            .HasColumnName("revoked_at")
            .HasColumnType("timestamptz");

        builder.Property(o => o.CreatedBy)
            .HasColumnName("created_by");

        builder.Property(o => o.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        builder.HasIndex(o => new { o.UserId, o.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_one_time_passwords_user_created");

        // OTP rows are user-scoped children and disappear with the user.
        // `created_by` (the issuing admin) is a soft reference — §6 does not
        // label it FK — so no constraint is mapped for it.
        builder.HasOne(o => o.User)
            .WithMany(u => u.OneTimePasswords)
            .HasForeignKey(o => o.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}