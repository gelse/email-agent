using EmailAgent.Core.Audit;
using EmailAgent.Core.Identity;
using EmailAgent.Core.Security;
using EmailAgent.Infrastructure.Audit;
using EmailAgent.Infrastructure.Identity;
using EmailAgent.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EmailAgent.Infrastructure;

/// <summary>M1T2 task: <c>AddInfrastructure</c> DI entry point (used by Program.cs).</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="EmailAgentDbContext"/> against the
    /// <c>ConnectionStrings:Postgres</c> configuration key (M1T2), the M1T3
    /// encryption service (<see cref="AddEncryption"/>), and the M1T4 auth
    /// services (<see cref="AddAuth"/>): the refresh-token store, the
    /// HMAC-chained audit writer and the JWT token issuer.
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // GetConnectionString reads ConnectionStrings:Postgres (the env-var
        // form maps onto it via the double-underscore rule); the literal key
        // lookup mirrors DbPingHealthCheck's fallback for hosts that add the
        // raw variable without the configuration provider mapping.
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? configuration["ConnectionStrings__Postgres"];

        services.AddDbContext<EmailAgentDbContext>(options =>
        {
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                options.UseNpgsql(connectionString);
            }
            // No connection string: the context stays unconfigured and the
            // `db` health check reports Unhealthy instead of crashing startup
            // (tests boot the host without one).
        });

        services.AddEncryption(configuration);
        services.AddAuth(configuration);

        return services;
    }

    /// <summary>
    /// M1T3: registers the singleton <see cref="IEncryptionService"/>
    /// (Argon2id + AES-GCM) and binds <c>EncryptionOptions</c> from the
    /// <c>System</c> section, so the documented <c>SYSTEM__KeyEncryptionKey</c>
    /// env var (§16 / SQ-3 / Q-E7) is the source of truth. The key is
    /// validated at host start (<c>ValidateOnStart</c>): a missing or
    /// &lt; 32-byte key fails startup with a clear error instead of deferring
    /// the failure to the first wrap/unwrap call.
    /// </summary>
    public static IServiceCollection AddEncryption(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<EncryptionOptions>()
            .Bind(configuration.GetSection(EncryptionOptions.SectionName))
            .Validate(
                options => EncryptionOptions.IsValid(options.KeyEncryptionKey),
                EncryptionOptions.InvalidKeyMessage)
            .ValidateOnStart();

        services.AddSingleton<IEncryptionService, Argon2idAesGcmEncryptionService>();

        return services;
    }

    /// <summary>
    /// M1T4: registers <see cref="IRefreshTokenStore"/>, <see cref="IAuditLogger"/>
    /// and the JWT <c>Auth</c> options binding (<c>AUTH__*</c>: SigningKey,
    /// Issuer, Audience, AccessTokenTtl, RefreshTokenTtl). The chain key is
    /// required at host start (<c>ValidateOnStart</c>) — an install cannot
    /// append audit rows without <c>AUDIT__HmacKey</c> (§16 / SQ-2).
    /// M1T5 adds the scoped <see cref="IOtpService"/> and the
    /// <c>Otp:Lifetime</c> binding (<c>OTP__Lifetime</c>, default 7 days,
    /// Q-A2).
    /// </summary>
    public static IServiceCollection AddAuth(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AuditOptions>()
            .Bind(configuration.GetSection(AuditOptions.SectionName))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.HmacKey),
                "Audit:HmacKey (env AUDIT__HmacKey) must be present (§16 / SQ-2 / Q-O10).")
            .ValidateOnStart();

        services.AddOptions<OtpOptions>()
            .Bind(configuration.GetSection(OtpOptions.SectionName))
            .Validate(
                options => options.Lifetime > TimeSpan.Zero,
                "Otp:Lifetime (env OTP__Lifetime) must be positive — a non-positive "
                + "lifetime would issue already-expired OTPs (Q-A2).")
            .ValidateOnStart();

        services.AddScoped<IRefreshTokenStore, RefreshTokenStore>();
        services.AddScoped<IAuditLogger, AuditLogger>();
        services.AddScoped<IOtpService, OtpService>();

        return services;
    }
}
