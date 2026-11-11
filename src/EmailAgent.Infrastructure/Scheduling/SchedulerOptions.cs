namespace EmailAgent.Infrastructure.Scheduling;

/// <summary>
/// Scheduler knobs (M5T2, SQ-M5-6 / Q-O4). Binds the <c>Scheduler</c>
/// configuration section, i.e. the documented <c>SCHEDULER__TickInterval</c>,
/// <c>SCHEDULER__MinInterval</c> and <c>SCHEDULER__JitterPercent</c>
/// environment variables (§16 — recorded as Q-O19 for the follow-up plan
/// change).
/// </summary>
/// <remarks>
/// Validation runs per the <c>CryptoSessionOptions</c> precedent: the
/// <c>ValidateOnStart</c> predicate registered with the options binding (the
/// host refuses to start on a non-positive tick/floor or an out-of-range
/// jitter) and the defaults on the property initializers.
/// </remarks>
public sealed class SchedulerOptions
{
    /// <summary>Configuration section name (<c>SCHEDULER__*</c> env vars land here).</summary>
    public const string SectionName = "Scheduler";

    /// <summary>DEFAULT for <c>SCHEDULER__TickInterval</c> — 1 minute (§10 "polls every minute").</summary>
    public static readonly TimeSpan DefaultTickInterval = TimeSpan.FromMinutes(1);

    /// <summary>DEFAULT for <c>SCHEDULER__MinInterval</c> — 1 minute (the Q-O4 floor).</summary>
    public static readonly TimeSpan DefaultMinInterval = TimeSpan.FromMinutes(1);

    /// <summary>DEFAULT for <c>SCHEDULER__JitterPercent</c> — ±10% (Q-O4).</summary>
    public const int DefaultJitterPercent = 10;

    /// <summary>Failure message shared by the options validator and startup guard.</summary>
    public const string InvalidMessage =
        "Scheduler:TickInterval (env SCHEDULER__TickInterval) and Scheduler:MinInterval "
        + "(env SCHEDULER__MinInterval) must both be positive and Scheduler:JitterPercent "
        + "(env SCHEDULER__JitterPercent) must be within [0, 100] — a non-positive interval "
        + "would busy-loop the scheduler and an out-of-range jitter would break the Q-O4 "
        + "±10% herd-spreading contract (SQ-M5-6).";

    /// <summary>How often the tick loop wakes (DEFAULT <c>00:01:00</c>, SQ-M5-6).</summary>
    public TimeSpan TickInterval { get; set; } = DefaultTickInterval;

    /// <summary>
    /// The Q-O4 floor: a per-source <c>interval_minutes</c> below this is
    /// clamped up, so no source is ever ticked faster (DEFAULT
    /// <c>00:01:00</c>, SQ-M5-6).
    /// </summary>
    public TimeSpan MinInterval { get; set; } = DefaultMinInterval;

    /// <summary>
    /// Symmetric jitter around the clamped interval, in percent
    /// (DEFAULT <c>10</c> = ±10%, Q-O4).
    /// </summary>
    public int JitterPercent { get; set; } = DefaultJitterPercent;

    /// <summary>Whether the knobs are sane — used by the <c>ValidateOnStart</c> predicate.</summary>
    public static bool IsValid(SchedulerOptions options)
        => options is not null
           && options.TickInterval > TimeSpan.Zero
           && options.MinInterval > TimeSpan.Zero
           && options.JitterPercent is >= 0 and <= 100;
}
