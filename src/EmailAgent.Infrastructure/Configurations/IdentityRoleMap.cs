using EmailAgent.Infrastructure.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmailAgent.Infrastructure.Configurations;

/// <summary>
/// ASP.NET Core Identity role table (M1T4 / SQ-10, task step 2):
/// <c>AspNetRoles</c> under Identity's default name (SQ-10), columns in the
/// repo's snake_case convention. Roles are <c>Admin</c> and <c>User</c> — no
/// separate <c>roles</c> table is needed beyond this one.
/// </summary>
internal sealed class IdentityRoleMap : IEntityTypeConfiguration<IdentityRole<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityRole<Guid>> builder)
    {
        builder.ToTable("AspNetRoles");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(r => r.Name).HasColumnName("name");
        builder.Property(r => r.NormalizedName)
            .HasColumnName("normalized_name")
            .IsRequired();

        builder.Property(r => r.ConcurrencyStamp).HasColumnName("concurrency_stamp");

        builder.HasIndex(r => r.NormalizedName)
            .IsUnique()
            .HasDatabaseName("ux_aspnet_roles_normalized_name");
    }
}