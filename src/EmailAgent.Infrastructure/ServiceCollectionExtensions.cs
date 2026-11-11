using EmailAgent.Core.Audit;
using EmailAgent.Core.Identity;
using EmailAgent.Core.Security;
using EmailAgent.Infrastructure.Audit;
using EmailAgent.Infrastructure.Identity;
using EmailAgent.Infrastructure.Scheduling;
using EmailAgent.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace EmailAgent.Infrastructure;

/// <summary>M1T2 task: <c>AddInfrastructure</c> DI entry point (used by Program.cs).</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="EmailAgentDbContext"/> against the
    /// <c>ConnectionStrings:Postgres</c> configuration key (M1T2), the M1T3
    /// encryption service (<see cref="AddEncryption"/>), the M1T4 auth
    /// services (<see cref="AddAuth"/>): the refresh-token store, the
    /// HMAC-chained audit writer and the JWT token issuer, the M5T1
    /// crypto-session store (<see cref="AddCryptoSessions"/>) and the M5T2
    /// scheduler hosted service + bounded job channel (<see cref="AddScheduling"/>).
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
        services.AddCryptoSessions(configuration);

        // M5T2: the scheduler hosted service + the bounded job channel
        // (SCHEDULER__* / WORKERS__ChannelCapacity — SQ-M5-6 / Q-O4).
        services.AddScheduling(configuration);

        return services;
    }

    /// <summary>
    /// M5T1 (SQ-M5-1 / SQ-M5-6): registers the shared <c>IMemoryCache</c>, the
    /// <b>singleton</b> <see cref="ICryptoSessionStore"/> (one session set per
    /// process — <c>IMemoryCache</c> is shared) and the
    /// <c>SESSION__IdleTimeout</c> / <c>SESSION__AbsoluteTimeout</c> binding
    /// from the <c>Session</c> section, validated at host start
    /// (<c>ValidateOnStart</c>): a non-positive timeout fails startup instead
    /// of issuing already-expired sessions.
    /// </summary>
    public static IServiceCollection AddCryptoSessions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddMemoryCache();

        services.AddOptions<CryptoSessionOptions>()
            .Bind(configuration.GetSection(CryptoSessionOptions.SectionName))
            .Validate(CryptoSessionOptions.IsValid, CryptoSessionOptions.InvalidMessage)
            .ValidateOnStart();

        services.AddSingleton<ICryptoSessionStore, MemoryCryptoSessionStore>();

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

    /// <summary>
    /// M5T2 (SQ-M5-6 / Q-O4): binds the <c>Scheduler</c> section
    /// (<c>SCHEDULER__TickInterval</c> / <c>SCHEDULER__MinInterval</c> /
    /// <c>SCHEDULER__JitterPercent</c>) onto <see cref="SchedulerOptions"/>
    /// and the <c>Workers</c> section's <c>WORKERS__ChannelCapacity</c> onto
    /// <see cref="JobQueueOptions"/>, both validated at host start
    /// (<c>ValidateOnStart</c>): a non-positive tick interval, a non-positive
    /// floor, a <c>JitterPercent</c> outside <c>[0, 100]</c>, or a
    /// non-positive channel capacity fails startup instead of busy-looping
    /// the scheduler or binding an unusable channel. Registers the singleton
    /// <see cref="IJobQueue"/> (the bounded <see cref="ChannelJobQueue"/>
    /// shared by the M5T2 producer and M5T3's consumers), the
    /// <see cref="IJitterSource"/> seam, the <see cref="TimeProvider"/> clock
    /// seam (<c>TryAdd</c> — Program.cs registers its own), and the
    /// <see cref="SchedulerHostedService"/> hosted service itself.
    /// </summary>
    public static IServiceCollection AddScheduling(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<SchedulerOptions>()
            .Bind(configuration.GetSection(SchedulerOptions.SectionName))
            .Validate(SchedulerOptions.IsValid, SchedulerOptions.InvalidMessage)
            .ValidateOnStart();

        services.AddOptions<JobQueueOptions>()
            .Bind(configuration.GetSection(JobQueueOptions.SectionName))
            .Validate(JobQueueOptions.IsValid, JobQueueOptions.InvalidMessage)
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IJobQueue, ChannelJobQueue>();
        services.AddSingleton<IJitterSource, RandomJitterSource>();
        services.AddHostedService<SchedulerHostedService>();

        return services;
    }
}
