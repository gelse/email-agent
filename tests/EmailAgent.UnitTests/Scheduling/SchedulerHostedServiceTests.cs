using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using EmailAgent.Infrastructure.Scheduling;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EmailAgent.UnitTests.Scheduling;

/// <summary>
/// M5T2 unit coverage for the scheduler (EF InMemory + real
/// <see cref="ChannelJobQueue"/> where the channel is under test, a fixed
/// <see cref="TimeProvider"/> and a deterministic <see cref="IJitterSource"/>
/// for the timing/jitter facts). No host, no network, no production service
/// (AGENTS.md test constraint) — a tick is driven directly through the
/// internal <c>RunTickAsync</c> seam, so nothing here waits on the real
/// 1-minute timer; the two loop facts use a short/long interval instead of
/// sleeps against the default.
/// </summary>
public sealed class SchedulerHostedServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly DbContextOptions<SchedulerTestDbContext> _dbOptions;
    private readonly ServiceProvider _provider;
    private readonly FixedTimeProvider _clock = new(Now);

    public SchedulerHostedServiceTests()
    {
        _dbOptions = new DbContextOptionsBuilder<SchedulerTestDbContext>()
            .UseInMemoryDatabase($"m5t2-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        var services = new ServiceCollection();
        services.AddScoped<EmailAgentDbContext>(_ => new SchedulerTestDbContext(_dbOptions));
        _provider = services.BuildServiceProvider();
    }

    public void Dispose() => _provider.Dispose();

    // -----------------------------------------------------------------------
    // EC1 (milestone Verification strategy): SchedulerTick_InsertsJobAndAdvancesNextRun_WithinJitterBounds
    // and PausedOrFutureSource_Skipped are the two mandated facts.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SchedulerTick_InsertsJobAndAdvancesNextRun_WithinJitterBounds()
    {
        var source = SeedSource(Now.AddMinutes(-2), intervalMinutes: 5);
        var queue = new ChannelJobQueue(Options.Create(new JobQueueOptions()));

        await CreateScheduler(queue: queue, jitter: new SequenceJitterSource(0.4))
            .RunTickAsync(CancellationToken.None);

        using var db = NewDb();
        var scheduled = await db.MessageSources.SingleAsync(s => s.Id == source.Id);
        var job = await db.ScheduledJobs.SingleAsync(j => j.SourceId == source.Id);

        job.Status.Should().Be(ScheduledJobStatus.Pending, "the producer enqueues Pending rows (§10 step 1)");
        job.Attempt.Should().Be(0);
        job.ScheduledFor.Should().Be(Now, "scheduled_for is the tick's captured now");
        job.CreatedAt.Should().Be(Now);
        scheduled.LastRunAt.Should().Be(Now);

        scheduled.NextRunAt.Should().NotBeNull();
        var advanced = scheduled.NextRunAt!.Value;
        advanced.Should().BeOnOrAfter(Now + TimeSpan.FromMinutes(5) * 0.9,
            "Q-O4: never below interval × (1 − 10%)");
        advanced.Should().BeOnOrBefore(Now + TimeSpan.FromMinutes(5) * 1.1,
            "Q-O4: never above interval × (1 + 10%)");
        advanced.Should().BeAfter(Now.AddMinutes(-2), "the stale cadence must be pushed into the future");

        queue.ApproximateDepth.Should().Be(1, "the committed job is handed to the bounded channel");
    }

    [Fact]
    public async Task PausedOrFutureSource_Skipped()
    {
        var paused = SeedSource(Now.AddMinutes(-5), status: MessageSourceStatus.Paused);
        var pausedFresh = SeedSource(nextRunAt: null, status: MessageSourceStatus.Paused);
        var errored = SeedSource(Now.AddMinutes(-5), status: MessageSourceStatus.Error);
        var future = SeedSource(Now.AddMinutes(30), status: MessageSourceStatus.Active);
        var queue = new ChannelJobQueue(Options.Create(new JobQueueOptions()));

        await CreateScheduler(queue: queue).RunTickAsync(CancellationToken.None);

        using var db = NewDb();
        db.ScheduledJobs.Should().BeEmpty("only a due Active source may produce a job row");

        var reloaded = await db.MessageSources.ToListAsync();
        reloaded.Single(s => s.Id == paused.Id).NextRunAt.Should().Be(Now.AddMinutes(-5), "a Paused source's cadence is untouched");
        reloaded.Single(s => s.Id == pausedFresh.Id).NextRunAt.Should().BeNull("status filters first — a NULL next_run_at Paused source is still not due");
        reloaded.Single(s => s.Id == errored.Id).NextRunAt.Should().Be(Now.AddMinutes(-5), "an Error source is skipped");
        reloaded.Single(s => s.Id == future.Id).NextRunAt.Should().Be(Now.AddMinutes(30), "an Active source with next_run_at > now is not due");

        queue.ApproximateDepth.Should().Be(0, "nothing was enqueued");
    }

    [Fact]
    public async Task ActiveSourceWithNullNextRun_Scheduled()
    {
        // A source created by M2T5 has next_run_at NULL — treated as due-now
        // and set on the first tick (task-file risk note).
        var source = SeedSource(nextRunAt: null, intervalMinutes: 5);

        await CreateScheduler().RunTickAsync(CancellationToken.None);

        using var db = NewDb();
        var scheduled = await db.MessageSources.SingleAsync(s => s.Id == source.Id);
        db.ScheduledJobs.Should().ContainSingle(j => j.SourceId == source.Id);
        scheduled.NextRunAt.Should().Be(Now + TimeSpan.FromMinutes(5),
            "jitter 0 → exactly now + interval_minutes on the first tick");
        scheduled.LastRunAt.Should().Be(Now);
    }

    [Fact]
    public async Task Jitter_StaysWithinConfiguredPercent_BothSigns()
    {
        var source = SeedSource(Now.AddMinutes(-1), intervalMinutes: 5);
        var scheduler = CreateScheduler(jitter: new SequenceJitterSource(1, -1));

        await scheduler.RunTickAsync(CancellationToken.None);
        var first = ReadNextRunAt(source.Id);

        // Re-due the source and retire the first job to a terminal state so
        // the next tick's in-flight suppression does not hide the source.
        using (var db = NewDb())
        {
            var tracked = await db.MessageSources.SingleAsync(s => s.Id == source.Id);
            tracked.NextRunAt = Now.AddMinutes(-1);
            var firstJob = await db.ScheduledJobs.SingleAsync(j => j.SourceId == source.Id);
            firstJob.Status = ScheduledJobStatus.Succeeded;
            await db.SaveChangesAsync();
        }

        await scheduler.RunTickAsync(CancellationToken.None);
        var second = ReadNextRunAt(source.Id);

        // The ±JitterPercent bounds are both reachable: the sequence draws
        // +1 then −1, scaled by 10% of the 5-minute interval.
        first.Should().Be(Now + TimeSpan.FromMinutes(5) * 1.1, "the +10% jitter bound is reachable");
        second.Should().Be(Now + TimeSpan.FromMinutes(5) * 0.9, "the −10% jitter bound is reachable");
    }

    [Fact]
    public async Task IntervalBelowFloor_ClampedToMinInterval()
    {
        // interval_minutes below the SCHEDULER__MinInterval floor (default
        // 1 min, Q-O4) is clamped up — with jitter 0 the advance is exact.
        var source = SeedSource(Now.AddMinutes(-1), intervalMinutes: 0);

        await CreateScheduler().RunTickAsync(CancellationToken.None);

        using var db = NewDb();
        var scheduled = await db.MessageSources.SingleAsync(s => s.Id == source.Id);
        scheduled.NextRunAt.Should().Be(Now + SchedulerOptions.DefaultMinInterval,
            "Q-O4: a source configured below the 1-minute floor is never ticked faster than the floor");
    }

    [Fact]
    public async Task SourceWithInFlightJob_Skipped_NoDuplicateEnqueue()
    {
        var pendingHolding = SeedSource(Now.AddMinutes(-1));
        var runningHolding = SeedSource(Now.AddMinutes(-1));
        var free = SeedSource(Now.AddMinutes(-1));
        SeedJob(pendingHolding.Id, ScheduledJobStatus.Pending);
        SeedJob(runningHolding.Id, ScheduledJobStatus.Running);
        var queue = new ChannelJobQueue(Options.Create(new JobQueueOptions()));

        await CreateScheduler(queue: queue).RunTickAsync(CancellationToken.None);

        using var db = NewDb();
        var jobs = await db.ScheduledJobs.ToListAsync();
        jobs.Should().HaveCount(3, "only the source without an in-flight job gets a new row");
        jobs.Count(j => j.SourceId == pendingHolding.Id).Should().Be(1, "the Pending row is the cadence marker");
        jobs.Count(j => j.SourceId == runningHolding.Id).Should().Be(1, "Running is non-terminal too");
        jobs.Count(j => j.SourceId == free.Id).Should().Be(1);

        var reloaded = await db.MessageSources.ToListAsync();
        reloaded.Single(s => s.Id == pendingHolding.Id).NextRunAt.Should().Be(Now.AddMinutes(-1),
            "a suppressed source's cadence does not advance");
        reloaded.Single(s => s.Id == runningHolding.Id).NextRunAt.Should().Be(Now.AddMinutes(-1));
        reloaded.Single(s => s.Id == free.Id).NextRunAt.Should().BeAfter(Now);

        queue.ApproximateDepth.Should().Be(1, "exactly one JobItem — no duplicate enqueue");
    }

    [Fact]
    public async Task Tick_EnqueuesJobItem_ToBoundedChannel()
    {
        var source = SeedSource(Now.AddMinutes(-1), intervalMinutes: 5);
        var queue = new ChannelJobQueue(Options.Create(new JobQueueOptions { ChannelCapacity = 8 }));

        await CreateScheduler(queue: queue).RunTickAsync(CancellationToken.None);

        queue.ApproximateDepth.Should().Be(1);

        await using var enumerator = queue.ReadAllAsync(CancellationToken.None)
            .GetAsyncEnumerator(CancellationToken.None);
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        var item = enumerator.Current;

        using var db = NewDb();
        var job = await db.ScheduledJobs.SingleAsync(j => j.SourceId == source.Id);
        item.JobId.Should().Be(job.Id, "the JobItem names the committed scheduled_jobs row");
        item.SourceId.Should().Be(source.Id);
        item.UserId.Should().Be(source.UserId, "M5T3's per-user WFQ groups on the owner");
    }

    [Fact]
    public async Task DistinctSources_ProcessedIndependently()
    {
        var blockedSource = SeedSource(Now.AddMinutes(-1));
        var otherSource = SeedSource(Now.AddMinutes(-1));
        var queue = new CoordinatedQueue(blockedSource.Id);
        var scheduler = CreateScheduler(queue: queue);

        var tick = scheduler.RunTickAsync(CancellationToken.None);

        // The blocked source reaches its enqueue and waits there...
        await queue.BlockedSourceEntered.WaitAsync(TimeSpan.FromSeconds(10));

        // ...while the other source's unit completes its enqueue concurrently.
        await queue.OtherSourceEnqueued.WaitAsync(TimeSpan.FromSeconds(10));
        tick.IsCompleted.Should().BeFalse(
            "a source blocked at its enqueue may hold only its own per-source gate");

        queue.Release();
        await tick.WaitAsync(TimeSpan.FromSeconds(10));

        queue.Items.Should().HaveCount(2, "both due sources were scheduled despite one blocking");
        using var db = NewDb();
        var sources = await db.MessageSources.ToListAsync();
        sources.Should().OnlyContain(s => s.NextRunAt > Now,
            "each independent unit committed its own advance");
    }

    [Fact]
    public async Task RunTick_Failure_DoesNotKillLoop()
    {
        var services = new ServiceCollection();
        services.AddScoped<EmailAgentDbContext>(
            _ => throw new InvalidOperationException("simulated database failure"));
        using var provider = services.BuildServiceProvider();

        var scheduler = new SchedulerHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ChannelJobQueue(Options.Create(new JobQueueOptions())),
            Options.Create(new SchedulerOptions { TickInterval = TimeSpan.FromMilliseconds(20) }),
            TimeProvider.System,
            new SequenceJitterSource(0),
            NullLogger<SchedulerHostedService>.Instance);

        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (scheduler.TicksAttempted < 2 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            scheduler.ExecuteTask.Should().NotBeNull();
            scheduler.TicksAttempted.Should().BeGreaterThanOrEqualTo(2,
                "each failing tick is logged and the loop keeps waking");
            scheduler.ExecuteTask!.IsFaulted.Should().BeFalse(
                "a failed tick must not escape ExecuteAsync");
            scheduler.ExecuteTask.IsCompleted.Should().BeFalse();
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }

        scheduler.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue(
            "shutdown after failed ticks still exits through the normal path");
    }

    [Fact]
    public async Task Shutdown_CompletesQueueWriter()
    {
        var queue = new ChannelJobQueue(Options.Create(new JobQueueOptions()));
        var buffered = new JobItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await queue.EnqueueAsync(buffered, CancellationToken.None);

        // 1-hour interval: no tick can elapse inside this test, so the facts
        // observe shutdown itself rather than racing the timer.
        var scheduler = CreateScheduler(
            queue: queue,
            options: new SchedulerOptions { TickInterval = TimeSpan.FromHours(1) });

        await scheduler.StartAsync(CancellationToken.None);
        await scheduler.StopAsync(CancellationToken.None);

        scheduler.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
        scheduler.TicksAttempted.Should().Be(0,
            "stopping must not trigger a scheduling tick");

        // A M5T3-style consumer drains: the writer completed, so the
        // enumeration yields the buffered item and then ENDS instead of
        // waiting forever (§4 graceful drain hand-off).
        var drained = new List<JobItem>();
        await foreach (var jobItem in queue.ReadAllAsync(CancellationToken.None))
        {
            drained.Add(jobItem);
        }

        drained.Should().ContainSingle().Which.Should().Be(buffered);
        queue.ApproximateDepth.Should().Be(0);
    }

    // -----------------------------------------------------------------------
    // Stubs and scaffolding — EF InMemory, deterministic clock/jitter, fakes.
    // -----------------------------------------------------------------------

    private SchedulerTestDbContext NewDb() => new(_dbOptions);

    private SchedulerHostedService CreateScheduler(
        IJobQueue? queue = null,
        IJitterSource? jitter = null,
        SchedulerOptions? options = null)
        => new(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            queue ?? new ChannelJobQueue(Options.Create(new JobQueueOptions())),
            Options.Create(options ?? new SchedulerOptions()),
            _clock,
            jitter ?? new SequenceJitterSource(0),
            NullLogger<SchedulerHostedService>.Instance);

    private MessageSource SeedSource(
        DateTimeOffset? nextRunAt,
        MessageSourceStatus status = MessageSourceStatus.Active,
        int intervalMinutes = 5)
    {
        using var db = NewDb();
        var source = new MessageSource
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            TypeId = "imap",
            DisplayName = $"m5t2-source-{Guid.NewGuid():N}",
            ConfigBlobId = Guid.NewGuid(),
            IntervalMinutes = intervalMinutes,
            Status = status,
            NextRunAt = nextRunAt,
            CreatedAt = Now,
        };
        db.MessageSources.Add(source);
        db.SaveChanges();
        return source;
    }

    private void SeedJob(Guid sourceId, ScheduledJobStatus status)
    {
        using var db = NewDb();
        db.ScheduledJobs.Add(new ScheduledJob
        {
            Id = Guid.NewGuid(),
            SourceId = sourceId,
            Status = status,
            Attempt = 0,
            ScheduledFor = Now.AddMinutes(-1),
            CreatedAt = Now.AddMinutes(-1),
        });
        db.SaveChanges();
    }

    private DateTimeOffset ReadNextRunAt(Guid sourceId)
    {
        using var db = NewDb();
        var nextRunAt = db.MessageSources.Single(s => s.Id == sourceId).NextRunAt;
        nextRunAt.Should().NotBeNull();
        return nextRunAt!.Value;
    }

    /// <summary>
    /// Deterministic clock: a fixed <c>GetUtcNow</c> for the tick's captured
    /// <c>now</c>, while timer callbacks (a running PeriodicTimer) delegate to
    /// the system clock so the two loop facts need no fake-timer plumbing.
    /// </summary>
    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
            => TimeProvider.System.CreateTimer(callback, state, dueTime, period);
    }

    /// <summary>
    /// Deterministic jitter: draws from a fixed sequence (cycling), so the
    /// ±bounds facts hit exact expected values instead of a random draw.
    /// </summary>
    private sealed class SequenceJitterSource : IJitterSource
    {
        private readonly double[] _values;
        private int _index;

        public SequenceJitterSource(params double[] values) => _values = values;

        public double NextJitter()
        {
            if (_values.Length == 0)
            {
                return 0;
            }

            var index = Interlocked.Increment(ref _index) - 1;
            return _values[index % _values.Length];
        }
    }

    /// <summary>
    /// Queue fake for <c>DistinctSources_ProcessedIndependently</c>: blocks
    /// ONE source's enqueue until released and signals when each source's
    /// enqueue ran — proving a blocked source holds only its own gate.
    /// </summary>
    private sealed class CoordinatedQueue : IJobQueue
    {
        private readonly Guid _blockedSourceId;
        private readonly TaskCompletionSource _blockedSourceEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _otherSourceEnqueued =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<JobItem> _items = new();

        public CoordinatedQueue(Guid blockedSourceId) => _blockedSourceId = blockedSourceId;

        public Task BlockedSourceEntered => _blockedSourceEntered.Task;

        public Task OtherSourceEnqueued => _otherSourceEnqueued.Task;

        public IReadOnlyCollection<JobItem> Items => _items.ToArray();

        public void Release() => _release.TrySetResult();

        public ValueTask EnqueueAsync(JobItem item, CancellationToken cancellationToken)
        {
            _items.Enqueue(item);
            if (item.SourceId == _blockedSourceId)
            {
                _blockedSourceEntered.TrySetResult();
                return new ValueTask(_release.Task.WaitAsync(cancellationToken));
            }

            _otherSourceEnqueued.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<JobItem> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // Not under test in this suite — never yields.
            await Task.CompletedTask;
            yield break;
        }

        public int ApproximateDepth => _items.Count;

        public void Complete()
        {
            // Not under test in this suite.
        }
    }

    /// <summary>
    /// EF Core 10 InMemory rejects unmapped <see cref="JsonDocument"/>
    /// properties (<c>User.KdfParams</c>) during model validation (M3T2/M4T1
    /// suite seam): a string converter keeps the model valid without touching
    /// the entities under test.
    /// </summary>
    private sealed class SchedulerTestDbContext(DbContextOptions<SchedulerTestDbContext> options)
        : EmailAgentDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    if (property.ClrType == typeof(JsonDocument))
                    {
                        property.SetValueConverter(new ValueConverter<JsonDocument?, string?>(
                            v => v == null ? null : v.RootElement.GetRawText(),
                            v => v == null ? null : JsonDocument.Parse(v, default)));
                    }
                }
            }
        }
    }
}
