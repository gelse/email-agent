using System.Collections.Concurrent;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmailAgent.Infrastructure.Scheduling;

/// <summary>
/// M5T2 (§4 hosted services / §10 job lifecycle step 1): the scheduler
/// producer. Every <see cref="SchedulerOptions.TickInterval"/> the loop wakes
/// and runs one <see cref="RunTickAsync"/>: the due-source snapshot
/// (<c>status='Active'</c> and <c>next_run_at IS NULL OR next_run_at &lt;=
/// now</c>, minus sources holding an in-flight non-terminal job) is taken
/// once, then each due source gets one atomic unit — a committed
/// <c>scheduled_jobs</c> row (<c>status='Pending'</c>, <c>attempt=0</c>,
/// <c>scheduled_for=now</c>) plus the <c>next_run_at</c> advance to
/// <c>now + interval × (1 ± jitter)</c> with the interval clamped to
/// <see cref="SchedulerOptions.MinInterval"/> (Q-O4) — and the committed job
/// is enqueued onto the bounded <see cref="IJobQueue"/> the M5T3 pool
/// drains. The scheduler reads/writes only the database and the channel: no
/// HTTP, engine, or source-plugin calls (Q-O13's <c>fetch-now</c> body
/// replacement is M5T5's).
/// </summary>
/// <remarks>
/// <para>
/// Concurrency (§4: "one scheduling tick per source runs serially; ticks do
/// not block each other"): the due set runs under <c>Task.WhenAll</c>; each
/// source unit owns its own DI scope (and therefore its own
/// <see cref="EmailAgentDbContext"/> — a DbContext is not safe for
/// concurrent operations) and serializes on a per-source
/// <see cref="SemaphoreSlim"/> keyed by source id. There is no global lock
/// across sources. The tick loop awaits each tick before the next
/// <see cref="PeriodicTimer"/> signal, so a source is never ticked twice
/// concurrently regardless of the guard (task-file step 4d) — the gate makes
/// that invariant explicit for any future overlapping caller.
/// </para>
/// <para>
/// Graceful shutdown (§4: stop scheduler, drain pool, flush logs):
/// <see cref="StopAsync"/> delegates the loop cancellation and in-flight-tick
/// wait to the base class, then completes the queue writer so M5T3's
/// consumers drain the remainder and observe completion.
/// </para>
/// </remarks>
public sealed class SchedulerHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IJobQueue _queue;
    private readonly IOptions<SchedulerOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly IJitterSource _jitter;
    private readonly ILogger<SchedulerHostedService> _logger;

    /// <summary>Per-source serial gates (§4) — one entry per source ever due.</summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _sourceGates = new();

    private int _ticksAttempted;

    public SchedulerHostedService(
        IServiceScopeFactory scopeFactory,
        IJobQueue queue,
        IOptions<SchedulerOptions> options,
        TimeProvider timeProvider,
        IJitterSource jitter,
        ILogger<SchedulerHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _options = options;
        _timeProvider = timeProvider;
        _jitter = jitter;
        _logger = logger;
    }

    /// <summary>
    /// Tick attempts started by the loop (counted before each
    /// <see cref="RunTickAsync"/>, successful or not) — the test seam for
    /// <c>RunTick_Failure_DoesNotKillLoop</c>.
    /// </summary>
    internal int TicksAttempted => Volatile.Read(ref _ticksAttempted);

    /// <summary>
    /// The PeriodicTimer loop: first wake one <c>TickInterval</c> after start,
    /// then one full tick per wake. A failing tick is logged and the loop
    /// continues — only cancellation ends it.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.Value.TickInterval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                Interlocked.Increment(ref _ticksAttempted);
                try
                {
                    await RunTickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Task-file step 4a: a failed tick must not kill the loop.
                    _logger.LogError(
                        ex,
                        "Scheduler tick failed at {Now}; the tick loop keeps running.",
                        _timeProvider.GetUtcNow());
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // §4 graceful shutdown: StopAsync cancelled the timer wait —
            // exit through the normal (non-faulted) path.
        }
    }

    /// <summary>
    /// One scheduling tick: snapshot the due sources, then schedule each one
    /// independently. Exposed as <c>internal</c> so the unit/integration
    /// suites (friend assemblies) can drive a tick without waiting on real
    /// timer intervals.
    /// </summary>
    internal async Task RunTickAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        List<DueSource> dueSources;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();

            // NULL next_run_at = a fresh source (M2T5 leaves it unset) = due
            // now — otherwise a new source would never be scheduled (task-file
            // risk note). The NOT EXISTS clause is the duplicate-enqueue
            // suppression: a source whose Pending/Running job has not reached
            // a terminal state is skipped, so a slow source can never pile up
            // unbounded Pending rows — the in-flight row is its cadence marker
            // (adopted rule; consequence recorded in the task file).
            dueSources = await db.MessageSources
                .Where(source => source.Status == MessageSourceStatus.Active
                    && (source.NextRunAt == null || source.NextRunAt <= now)
                    && !db.ScheduledJobs.Any(job => job.SourceId == source.Id
                        && (job.Status == ScheduledJobStatus.Pending
                            || job.Status == ScheduledJobStatus.Running)))
                .Select(source => new DueSource(source.Id, source.UserId, source.IntervalMinutes))
                .ToListAsync(cancellationToken);
        }

        if (dueSources.Count == 0)
        {
            return;
        }

        _logger.LogDebug(
            "Scheduler tick at {Now}: {DueCount} due source(s).",
            now,
            dueSources.Count);

        await Task.WhenAll(dueSources.Select(source => ScheduleSourceAsync(source, now, cancellationToken)));
    }

    /// <summary>
    /// Base-class cancellation + in-flight-tick wait, then the §4 drain
    /// hand-off: complete the queue writer so M5T3's consumers drain the
    /// remaining items and <see cref="IJobQueue.ReadAllAsync"/> ends.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken);
        }
        finally
        {
            // §4 drain hand-off runs unconditionally: even if the loop faulted
            // (e.g. a timer/options failure), consumers must observe completion
            // instead of waiting forever — while the original exception keeps
            // propagating so the fault stays visible to the host.
            _queue.Complete();
            _logger.LogInformation(
                "Scheduler stopped; job queue writer completed with {QueueDepth} item(s) pending — the worker pool drains to a quiescent state (§4 graceful shutdown).",
                _queue.ApproximateDepth);
        }
    }

    /// <summary>One source's serial unit: insert + advance in a transaction, then enqueue.</summary>
    private async Task ScheduleSourceAsync(DueSource source, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var gate = _sourceGates.GetOrAdd(source.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var options = _options.Value;

            // Q-O4: clamp to the floor so a source configured below
            // SCHEDULER__MinInterval is never ticked faster than it.
            var intervalMinutes = Math.Max(source.IntervalMinutes, options.MinInterval.TotalMinutes);

            // Q-O4 / SQ-M5-6: uniform ±JitterPercent% around the clamped
            // interval — herd spreading, not a hard guarantee.
            var jitter = _jitter.NextJitter() * (options.JitterPercent / 100.0);
            var nextRunAt = now + TimeSpan.FromMinutes(intervalMinutes) * (1 + jitter);

            var job = new ScheduledJob
            {
                Id = Guid.NewGuid(),
                SourceId = source.Id,
                Status = ScheduledJobStatus.Pending,
                Attempt = 0,
                ScheduledFor = now,
                CreatedAt = now,
            };

            await using (var scope = _scopeFactory.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();

                // Insert + advance in ONE transaction: a crash between the two
                // repeats a tick at worst (the in-flight row suppresses even
                // that) but can never skip one permanently (task-file step 4c).
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

                var messageSource = await db.MessageSources
                    .SingleOrDefaultAsync(existing => existing.Id == source.Id, cancellationToken);
                if (messageSource is null)
                {
                    // Deleted between the snapshot and this unit — nothing to
                    // schedule; the disposed transaction rolls back nothing.
                    return;
                }

                db.ScheduledJobs.Add(job);
                messageSource.NextRunAt = nextRunAt;
                messageSource.LastRunAt = now;
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            // Enqueue AFTER the commit: the channel may only carry jobs whose
            // row exists (M5T3 claims the Pending row this JobItem names).
            // FullMode = Wait back-pressures here instead of dropping.
            await _queue.EnqueueAsync(new JobItem(job.Id, source.Id, source.UserId), cancellationToken);

            _logger.LogDebug(
                "Scheduled job {JobId} for source {SourceId}; next_run_at -> {NextRunAt}.",
                job.Id,
                source.Id,
                nextRunAt);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Due-source snapshot row: identity for the gate/JobItem, interval for the advance math.</summary>
    private sealed record DueSource(Guid Id, Guid UserId, int IntervalMinutes);
}
