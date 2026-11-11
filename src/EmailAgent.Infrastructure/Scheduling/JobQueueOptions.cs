namespace EmailAgent.Infrastructure.Scheduling;

/// <summary>
/// Job-channel knob (M5T2, SQ-M5-6). Binds the <c>Workers</c> configuration
/// section, i.e. the documented <c>WORKERS__ChannelCapacity</c> environment
/// variable (§16 — recorded as Q-O19 for the follow-up plan change).
/// M5T3's <c>WorkerOptions</c> binds the remaining <c>WORKERS__*</c> knobs
/// from the same section.
/// </summary>
/// <remarks>
/// Validation runs per the <c>CryptoSessionOptions</c> precedent: the
/// <c>ValidateOnStart</c> predicate registered with the options binding (the
/// host refuses to start on a non-positive capacity) and the default on the
/// property initializer.
/// </remarks>
public sealed class JobQueueOptions
{
    /// <summary>Configuration section name (<c>WORKERS__*</c> env vars land here).</summary>
    public const string SectionName = "Workers";

    /// <summary>DEFAULT for <c>WORKERS__ChannelCapacity</c> — 256 jobs.</summary>
    public const int DefaultChannelCapacity = 256;

    /// <summary>Failure message shared by the options validator and startup guard.</summary>
    public const string InvalidMessage =
        "Workers:ChannelCapacity (env WORKERS__ChannelCapacity) must be positive — a "
        + "non-positive bound could never accept the scheduler's first job (SQ-M5-6).";

    /// <summary>Bounded channel capacity (DEFAULT <c>256</c>, SQ-M5-6).</summary>
    public int ChannelCapacity { get; set; } = DefaultChannelCapacity;

    /// <summary>Whether the capacity is positive — used by the <c>ValidateOnStart</c> predicate.</summary>
    public static bool IsValid(JobQueueOptions options)
        => options is not null && options.ChannelCapacity > 0;
}
