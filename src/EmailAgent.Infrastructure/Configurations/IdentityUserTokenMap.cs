using EmailAgent.Infrastructure.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmailAgent.Infrastructure.Configurations;

/// <summary>
/// <c>AspNetUserTokens</c> (M1T4 / SQ-10; Identity's default table name).
/// </summary>
internal sealed class IdentityUserTokenMap : IEntityTypeConfiguration<IdentityUserToken<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserToken<Guid>> builder)
    {
        builder.ToTable("AspNetUserTokens");

        builder.HasKey(t => new { t.UserId, t.LoginProvider, t.Name });

        builder.Property(t => t.UserId).HasColumnName("user_id");
        builder.Property(t => t.LoginProvider).HasColumnName("login_provider");
        builder.Property(t => t.Name).HasColumnName("name");
        builder.Property(t => t.Value).HasColumnName("value");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);
    }
}
