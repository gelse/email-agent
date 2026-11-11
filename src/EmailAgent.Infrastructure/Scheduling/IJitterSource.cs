namespace EmailAgent.Infrastructure.Scheduling;

/// <summary>
/// Jitter seam for the scheduler's <c>next_run_at</c> advance (M5T2 step 6):
/// returns a symmetric factor in <c>[-1, +1]</c> that
/// <see cref="SchedulerHostedService"/> scales by
/// <c>SCHEDULER__JitterPercent / 100</c> — Q-O4's herd-spreading jitter.
/// Production uses <see cref="RandomJitterSource"/>; tests inject a
/// deterministic sequence for the ±bounds facts.
/// </summary>
public interface IJitterSource
{
    /// <summary>Next jitter factor, uniform in <c>[-1, +1]</c>.</summary>
    double NextJitter();
}

/// <summary>
/// Production <see cref="IJitterSource"/>: uniform in <c>[-1, +1]</c> over
/// <see cref="Random.Shared"/> (thread-safe — the scheduler's per-source
/// units draw concurrently). The task file's "seeded Random" option: the
/// seam, not the instance, carries determinism (tests inject
/// <see cref="IJitterSource"/> fakes).
/// </summary>
public sealed class RandomJitterSource : IJitterSource
{
    /// <inheritdoc />
    public double NextJitter() => Random.Shared.NextDouble() * 2 - 1;
}
