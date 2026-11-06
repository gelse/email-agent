using EmailAgent.Infrastructure.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmailAgent.Infrastructure.Configurations;

/// <summary>
/// <c>AspNetRoleClaims</c> (M1T4 / SQ-10; Identity's default table name).
/// </summary>
internal sealed class IdentityRoleClaimMap : IEntityTypeConfiguration<IdentityRoleClaim<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityRoleClaim<Guid>> builder)
    {
        builder.ToTable("AspNetRoleClaims");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedOnAdd();

        builder.Property(c => c.RoleId).HasColumnName("role_id");
        builder.Property(c => c.ClaimType).HasColumnName("claim_type");
        builder.Property(c => c.ClaimValue).HasColumnName("claim_value");

        builder.HasIndex(c => c.RoleId)
            .HasDatabaseName("ix_aspnet_role_claims_role_id");
    }
}
