using EmailAgent.Infrastructure.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmailAgent.Infrastructure.Configurations;

/// <summary>
/// <c>AspNetUserClaims</c> (M1T4 / SQ-10; Identity's default table name;
/// unused in M1 but part of Identity's default schema). Columns follow the
/// repo's snake_case convention.
/// </summary>
internal sealed class IdentityUserClaimMap : IEntityTypeConfiguration<IdentityUserClaim<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserClaim<Guid>> builder)
    {
        builder.ToTable("AspNetUserClaims");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedOnAdd();

        builder.Property(c => c.UserId).HasColumnName("user_id");
        builder.Property(c => c.ClaimType).HasColumnName("claim_type");
        builder.Property(c => c.ClaimValue).HasColumnName("claim_value");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(c => c.UserId)
            .HasDatabaseName("ix_aspnet_user_claims_user_id");
    }
}
