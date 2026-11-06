using EmailAgent.Infrastructure.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmailAgent.Infrastructure.Configurations;

/// <summary>
/// <c>AspNetUserRoles</c> (M1T4 / SQ-10; Identity's default table name).
/// </summary>
internal sealed class IdentityUserRoleMap : IEntityTypeConfiguration<IdentityUserRole<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserRole<Guid>> builder)
    {
        builder.ToTable("AspNetUserRoles");

        builder.HasKey(r => new { r.UserId, r.RoleId });

        builder.Property(r => r.UserId).HasColumnName("user_id");
        builder.Property(r => r.RoleId).HasColumnName("role_id");

        builder.HasOne<IdentityRole<Guid>>()
            .WithMany()
            .HasForeignKey(r => r.RoleId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(r => r.UserId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => r.RoleId)
            .HasDatabaseName("ix_aspnet_user_roles_role_id");
    }
}
