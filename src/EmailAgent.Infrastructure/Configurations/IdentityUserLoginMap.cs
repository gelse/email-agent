using EmailAgent.Infrastructure.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmailAgent.Infrastructure.Configurations;

/// <summary>
/// <c>AspNetUserLogins</c> (M1T4 / SQ-10; Identity's default table name).
/// </summary>
internal sealed class IdentityUserLoginMap : IEntityTypeConfiguration<IdentityUserLogin<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserLogin<Guid>> builder)
    {
        builder.ToTable("AspNetUserLogins");

        builder.HasKey(l => new { l.LoginProvider, l.ProviderKey });

        builder.Property(l => l.LoginProvider).HasColumnName("login_provider");
        builder.Property(l => l.ProviderKey).HasColumnName("provider_key");
        builder.Property(l => l.ProviderDisplayName).HasColumnName("provider_display_name");
        builder.Property(l => l.UserId).HasColumnName("user_id");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(l => l.UserId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(l => l.UserId)
            .HasDatabaseName("ix_aspnet_user_logins_user_id");
    }
}