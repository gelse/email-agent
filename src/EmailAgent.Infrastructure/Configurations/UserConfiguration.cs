using EmailAgent.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmailAgent.Infrastructure.Configurations;

/// <summary>§6 <c>users</c> mapping (M1T2 task steps 3–4).</summary>
internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");

        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        // IdentityUser maps Email as string?; the §6 column stays NOT NULL
        // (InitialSchemaTests pins it), so IsRequired keeps the model honest.
        builder.Property(u => u.Email)
            .HasColumnName("email")
            .HasColumnType("citext")
            .IsRequired();

        // Enums are stored as text via HasConversion<string>() (the C# enum
        // name), with CHECK constraints mirroring §6's enum(...) values —
        // deliberately NOT HasPostgresEnum (M1T2 task step 9).
        builder.Property(u => u.Role)
            .HasColumnName("role")
            .HasColumnType("text")
            .HasConversion<string>();

        builder.Property(u => u.Status)
            .HasColumnName("status")
            .HasColumnType("text")
            .HasConversion<string>();

        // IdentityUser maps PasswordHash as string?; §6 keeps it NOT NULL.
        builder.Property(u => u.PasswordHash)
            .HasColumnName("password_hash")
            .IsRequired();

        builder.Property(u => u.MustChangePassword)
            .HasColumnName("must_change_password")
            .HasDefaultValue(true);
        // §6 default differs from the CLR default (false): make `true` the
        // sentinel so an explicit `false` is always written while "unset"
        // relies on the database default (EF omits sentinel-valued columns
        // from the INSERT — ModificationCommand.ColumnValuePropagator).
        builder.Property(u => u.MustChangePassword).Metadata.Sentinel = true;

        // Key material — all four NULL until first-login passphrase set
        // (Q-E6 / SQ-11 / Q-E8). No CHECK on purpose: the invariant is an
        // application-layer rule asserted against real data in M1T9.
        builder.Property(u => u.KdfSalt)
            .HasColumnName("kdf_salt")
            .HasColumnType("bytea");

        builder.Property(u => u.KdfParams)
            .HasColumnName("kdf_params")
            .HasColumnType("jsonb");

        builder.Property(u => u.DekWrapped)
            .HasColumnName("dek_wrapped")
            .HasColumnType("bytea");

        builder.Property(u => u.DekNonce)
            .HasColumnName("dek_nonce")
            .HasColumnType("bytea");

        builder.Property(u => u.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        builder.Property(u => u.LastLoginAt)
            .HasColumnName("last_login_at")
            .HasColumnType("timestamptz");

        // M1T4 / SQ-10 (Q-A11): Identity housekeeping columns. §6 does not
        // list them; Identity requires them, so they are additive to the
        // `users` row and created by this task's IdentitySchema migration.
        builder.Property(u => u.UserName)
            .HasColumnName("user_name");

        builder.Property(u => u.NormalizedUserName)
            .HasColumnName("normalized_user_name");

        builder.Property(u => u.EmailConfirmed)
            .HasColumnName("email_confirmed")
            .HasDefaultValue(false);

        builder.Property(u => u.NormalizedEmail)
            .HasColumnName("normalized_email");

        builder.Property(u => u.SecurityStamp)
            .HasColumnName("security_stamp");

        builder.Property(u => u.ConcurrencyStamp)
            .HasColumnName("concurrency_stamp");

        builder.Property(u => u.PhoneNumber)
            .HasColumnName("phone_number");

        builder.Property(u => u.PhoneNumberConfirmed)
            .HasColumnName("phone_number_confirmed");

        builder.Property(u => u.TwoFactorEnabled)
            .HasColumnName("two_factor_enabled");

        builder.Property(u => u.LockoutEnd)
            .HasColumnName("lockout_end");

        builder.Property(u => u.LockoutEnabled)
            .HasColumnName("lockout_enabled");

        builder.Property(u => u.AccessFailedCount)
            .HasColumnName("access_failed_count");

        builder.HasIndex(u => u.NormalizedUserName)
            .IsUnique()
            .HasDatabaseName("ux_users_normalized_user_name");

        builder.HasIndex(u => u.NormalizedEmail)
            .HasDatabaseName("ix_users_normalized_email");

        builder.HasIndex(u => u.Email)
            .IsUnique()
            .HasDatabaseName("ux_users_email");

        builder.ToTable("users", t =>
        {
            t.HasCheckConstraint("ck_users_role", "role IN ('Admin', 'User')");
            t.HasCheckConstraint("ck_users_status", "status IN ('Pending', 'Active', 'Disabled')");
        });
    }
}
