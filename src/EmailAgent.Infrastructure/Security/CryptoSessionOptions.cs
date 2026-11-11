namespace EmailAgent.Infrastructure.Security;

/// <summary>
/// Crypto-session timeout knobs (M5T1, SQ-M5-6). Binds the <c>Session</c>
/// configuration section, i.e. the documented <c>SESSION__IdleTimeout</c> and
/// <c>SESSION__AbsoluteTimeout</c> environment variables (§16 — recorded as
/// Q-O19 for the follow-up plan change).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IdleTimeout"/> is sliding: every user-data request and every
/// <c>TouchForUser</c> job tick resets it, never past
/// <see cref="AbsoluteTimeout"/>. <see cref="AbsoluteTimeout"/> is a hard cap
/// from session creation — a JWT refresh does not extend it (§8 / SQ-M5-10).
/// </para>
/// <para>
/// Validation runs twice, per the <c>EncryptionOptions</c> precedent: the
/// <c>ValidateOnStart</c> predicate registered with the options binding (the
/// host refuses to start on a non-positive timeout) and the defaults on the
/// property initializers.
/// </para>
/// </remarks>
public sealed class CryptoSessionOptions
{
    /// <summary>Configuration section name (<c>SESSION__*</c> env vars land here).</summary>
    public const string SectionName = "Session";

    /// <summary>DEFAULT for <c>SESSION__IdleTimeout</c> — 30 minutes.</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(30);

    /// <summary>DEFAULT for <c>SESSION__AbsoluteTimeout</c> — 12 hours.</summary>
    public static readonly TimeSpan DefaultAbsoluteTimeout = TimeSpan.FromHours(12);

    /// <summary>Failure message shared by the options validator and startup guard.</summary>
    public const string InvalidMessage =
        "Session:IdleTimeout and Session:AbsoluteTimeout (env SESSION__IdleTimeout / "
        + "SESSION__AbsoluteTimeout) must both be positive — a non-positive timeout would "
        + "issue already-expired crypto sessions (SQ-M5-6).";

    /// <summary>Sliding idle timeout (DEFAULT <c>00:30:00</c>, SQ-M5-6).</summary>
    public TimeSpan IdleTimeout { get; set; } = DefaultIdleTimeout;

    /// <summary>Hard absolute cap from session creation (DEFAULT <c>12:00:00</c>, SQ-M5-6).</summary>
    public TimeSpan AbsoluteTimeout { get; set; } = DefaultAbsoluteTimeout;

    /// <summary>Whether both timeouts are positive — used by the <c>ValidateOnStart</c> predicate.</summary>
    public static bool IsValid(CryptoSessionOptions options)
        => options is not null
           && options.IdleTimeout > TimeSpan.Zero
           && options.AbsoluteTimeout > TimeSpan.Zero;
}
