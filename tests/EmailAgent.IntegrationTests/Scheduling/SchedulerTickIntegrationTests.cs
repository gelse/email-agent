using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using EmailAgent.Infrastructure.Scheduling;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EmailAgent.IntegrationTests.Scheduling;

/// <summary>
/// Own Testcontainers Postgres for the scheduler facts (M4E2E pattern):
/// <c>RunTickAsync</c> queries <c>message_sources</c> globally, so a shared
/// container would let each fact schedule — and mutate
/// <c>next_run_at</c>/<c>scheduled_jobs</c> on — rows seeded by other test
/// classes, and would make this class's facts order-dependent on theirs.
/// </summary>
[CollectionDefinition("M5Scheduler", DisableParallelization = true)]
public sealed class M5SchedulerCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "M5Scheduler";
}

/// <summary>
/// M5T2 integration facts (Testcontainers Postgres, task file "Tests"): the
/// real due-source LINQ query and the in-flight <c>NOT EXISTS</c> suppression
/// against relational semantics — timestamptz round-trip and subquery
/// evaluation that EF InMemory cannot exercise. Each fact drives one real
/// <c>RunTickAsync</c> through the internal seam with the production
/// <see cref="RandomJitterSource"/> and <see cref="TimeProvider.System"/>, so
/// <c>next_run_at</c> assertions allow the full Q-O4 ±10% window around the
/// 5-minute interval. Seeded rows use whole-second timestamps (timestamptz is
/// microsecond-precision) so equality assertions survive the round-trip.
/// </summary>
[Collection(M5SchedulerCollection.Name)]
public sealed class SchedulerTickIntegrationTests(PostgresFixture postgres)
{
    private const int IntervalMinutes = 5;

    private static readonly SemaphoreSlim MigrationGate = new(1, 1);
    private static bool _migrated;

    private EmailAgentDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<EmailAgentDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;
        return new EmailAgentDbContext(opts);
    }

    private async Task EnsureMigratedAsync()
    {
        if (_migrated) return;
        await MigrationGate.WaitAsync();
        try
        {
            if (_migrated) return;
            await using var db = CreateDb();
            await db.Database.MigrateAsync();
            _migrated = true;
        }
        finally { MigrationGate.Release(); }
    }

    private static User MakeUser(Guid? id = null)
    {
        var uid = id ?? Guid.NewGuid();
        return new User
        {
            Id = uid,
            UserName = $"u-{uid:N}",
            NormalizedUserName = $"U-{uid:N}",
            Email = $"{uid:N}@test.local",
            NormalizedEmail = $"{uid:N}@TEST.LOCAL",
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
            PasswordHash = "m5t2-not-a-real-hash-integration-fixture",
            Role = UserRole.User,
            Status = UserStatus.Active,
            MustChangePassword = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>Truncates to whole seconds so seeded values survive the timestamptz round-trip exactly.</summary>
    private static DateTimeOffset WholeSeconds(DateTimeOffset value)
        => new(value.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, value.Offset);

    private async Task<Guid> SeedSourceAsync(DateTimeOffset? nextRunAt, int intervalMinutes = IntervalMinutes)
    {
        var user = MakeUser();
        var source = new MessageSource
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TypeId = "imap",
            DisplayName = $"m5t2-{Guid.NewGuid():N}"[..16],
            ConfigBlobId = Guid.NewGuid(),
            IntervalMinutes = intervalMinutes,
            Status = MessageSourceStatus.Active,
            NextRunAt = nextRunAt,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await using var db = CreateDb();
        db.Users.Add(user);
        db.EncryptedBlobs.Add(new EncryptedBlob
        {
            Id = source.ConfigBlobId,
            OwnerUserId = user.Id,
            Ciphertext = [1, 2, 3],
            Nonce = new byte[12],
            Purpose = "message_source_config",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.MessageSources.Add(source);
        await db.SaveChangesAsync();
        return source.Id;
    }

    private async Task<Guid> SeedJobAsync(Guid sourceId, ScheduledJobStatus status)
    {
        var job = new ScheduledJob
        {
            Id = Guid.NewGuid(),
            SourceId = sourceId,
            Status = status,
            Attempt = 0,
            ScheduledFor = WholeSeconds(DateTimeOffset.UtcNow.AddMinutes(-30)),
            CreatedAt = WholeSeconds(DateTimeOffset.UtcNow.AddMinutes(-30)),
        };
        await using var db = CreateDb();
        db.ScheduledJobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    private ServiceProvider BuildProvider() => new ServiceCollection()
        .AddLogging()
        .AddDbContext<EmailAgentDbContext>(options => options.UseNpgsql(postgres.ConnectionString))
        .AddScheduling(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Scheduler:TickInterval"] = "00:01:00",
            ["Scheduler:MinInterval"] = "00:01:00",
            ["Scheduler:JitterPercent"] = "10",
            ["Workers:ChannelCapacity"] = "256",
        }).Build())
        .BuildServiceProvider();

    /// <summary>
    /// The scheduler under test: production wiring (options binding, jitter,
    /// clock) from <see cref="ServiceCollectionExtensions.AddScheduling"/>, a
    /// dedicated channel per fact, and the real relational context behind the
    /// scope factory.
    /// </summary>
    private static SchedulerHostedService CreateScheduler(ServiceProvider provider, IJobQueue queue)
        => new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            queue,
            provider.GetRequiredService<IOptions<SchedulerOptions>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IJitterSource>(),
            NullLogger<SchedulerHostedService>.Instance);

    /// <summary>
    /// Generous capacity so the tick under test can never back-pressure on
    /// enqueue: the fact only drains its channel after <c>RunTickAsync</c>
    /// has returned, i.e. after every unit's <c>EnqueueAsync</c> completed.
    /// </summary>
    private static IJobQueue CreateQueue()
        => new ChannelJobQueue(Microsoft.Extensions.Options.Options.Create(
            new JobQueueOptions { ChannelCapacity = 4096 }));

    /// <summary>
    /// Non-blocking drain of what the completed tick buffered (RunTickAsync
    /// awaits all enqueues, so everything the scheduler produced is here).
    /// </summary>
    private static async Task<List<JobItem>> DrainAsync(IJobQueue queue)
    {
        var items = new List<JobItem>();
        while (queue.ApproximateDepth > 0)
        {
            await using var enumerator = queue.ReadAllAsync(CancellationToken.None)
                .GetAsyncEnumerator(CancellationToken.None);
            if (!await enumerator.MoveNextAsync())
            {
                break;
            }

            items.Add(enumerator.Current);
        }

        return items;
    }

    [Fact]
    public async Task DueSource_Query_InsertsAndAdvances_OnRealPostgres()
    {
        await EnsureMigratedAsync();
        var stale = WholeSeconds(DateTimeOffset.UtcNow.AddMinutes(-10));
        var sourceId = await SeedSourceAsync(stale);

        using var provider = BuildProvider();
        var queue = CreateQueue();
        var scheduler = CreateScheduler(provider, queue);

        var before = DateTimeOffset.UtcNow;
        await scheduler.RunTickAsync(CancellationToken.None);
        var after = DateTimeOffset.UtcNow;

        await using var db = CreateDb();
        var source = await db.MessageSources.SingleAsync(s => s.Id == sourceId);
        var jobs = await db.ScheduledJobs.Where(j => j.SourceId == sourceId).ToListAsync();

        // One committed row, Pending/attempt=0 — §10 job lifecycle step 1.
        jobs.Should().ContainSingle();
        var job = jobs[0];
        job.Status.Should().Be(ScheduledJobStatus.Pending);
        job.Attempt.Should().Be(0);
        job.ScheduledFor.Should().BeOnOrAfter(before.AddSeconds(-1), "scheduled_for is the tick's captured now");
        job.ScheduledFor.Should().BeOnOrBefore(after.AddSeconds(1));

        // last_run_at and scheduled_for are written from the same captured now
        // in one transaction — they survive the round-trip as identical values.
        source.LastRunAt.Should().NotBeNull();
        source.LastRunAt!.Value.Should().Be(job.ScheduledFor);

        // next_run_at advanced from the stale value by interval ±10% jitter.
        source.NextRunAt.Should().NotBeNull("the due source's cadence must advance");
        var nextRunAt = source.NextRunAt!.Value;
        nextRunAt.Should().BeAfter(stale, "the stale cadence must be pushed into the future");
        nextRunAt.Should().BeOnOrAfter(before + TimeSpan.FromMinutes(IntervalMinutes) * 0.9 - TimeSpan.FromSeconds(1),
            "Q-O4: never below interval × (1 − 10%) minus round-trip slack");
        nextRunAt.Should().BeOnOrBefore(after + TimeSpan.FromMinutes(IntervalMinutes) * 1.1 + TimeSpan.FromSeconds(1),
            "Q-O4: never above interval × (1 + 10%) plus round-trip slack");

        // The committed job is handed to the bounded channel with its owner.
        var items = await DrainAsync(queue);
        var forSource = items.Where(i => i.SourceId == sourceId).ToList();
        forSource.Should().ContainSingle("exactly one JobItem per scheduled job — no duplicate enqueue");
        forSource[0].JobId.Should().Be(job.Id, "the JobItem names the committed scheduled_jobs row");
        forSource[0].UserId.Should().Be(source.UserId);
    }

    [Fact]
    public async Task InFlightSuppression_RelationalPredicate()
    {
        await EnsureMigratedAsync();
        var due = WholeSeconds(DateTimeOffset.UtcNow.AddMinutes(-10));
        var pendingSource = await SeedSourceAsync(due);
        var runningSource = await SeedSourceAsync(due);
        var pendingJobId = await SeedJobAsync(pendingSource, ScheduledJobStatus.Pending);
        var runningJobId = await SeedJobAsync(runningSource, ScheduledJobStatus.Running);

        using var provider = BuildProvider();
        var queue = CreateQueue();
        var scheduler = CreateScheduler(provider, queue);

        await scheduler.RunTickAsync(CancellationToken.None);

        await using var db = CreateDb();
        foreach (var (sourceId, heldJobId) in new[] { (pendingSource, pendingJobId), (runningSource, runningJobId) })
        {
            // The NOT EXISTS subquery suppresses the source while a
            // Pending/Running row exists — on real Postgres, against the
            // real correlated subquery.
            var jobs = await db.ScheduledJobs.Where(j => j.SourceId == sourceId).ToListAsync();
            jobs.Should().ContainSingle("the in-flight row is the cadence marker — no new job may be inserted");
            jobs[0].Id.Should().Be(heldJobId);

            var source = await db.MessageSources.SingleAsync(s => s.Id == sourceId);
            source.NextRunAt.Should().Be(due, "a suppressed source's cadence does not advance");
            source.LastRunAt.Should().BeNull("no scheduling unit ran for this source");
        }

        // Nothing naming either suppressed source may have been enqueued:
        // EnqueueAsync happens only after the unit's commit, so a wrongly
        // scheduled source would already be buffered when the tick returned.
        var items = await DrainAsync(queue);
        items.Should().NotContain(i => i.SourceId == pendingSource || i.SourceId == runningSource,
            "suppressed sources must not reach the M5T3 worker pool");
    }

    [Fact]
    public async Task NullNextRunAt_Scheduled_ThenAdvanced()
    {
        await EnsureMigratedAsync();
        // M2T5 leaves next_run_at NULL on creation — the due query treats it
        // as due-now, otherwise a fresh source would never be scheduled.
        var sourceId = await SeedSourceAsync(nextRunAt: null);

        using var provider = BuildProvider();
        var queue = CreateQueue();
        var scheduler = CreateScheduler(provider, queue);

        var before = DateTimeOffset.UtcNow;
        await scheduler.RunTickAsync(CancellationToken.None);
        var after = DateTimeOffset.UtcNow;

        await using var db = CreateDb();
        var source = await db.MessageSources.SingleAsync(s => s.Id == sourceId);
        var jobs = await db.ScheduledJobs.Where(j => j.SourceId == sourceId).ToListAsync();

        jobs.Should().ContainSingle();
        var job = jobs[0];
        job.Status.Should().Be(ScheduledJobStatus.Pending);
        job.ScheduledFor.Should().BeOnOrAfter(before.AddSeconds(-1));
        job.ScheduledFor.Should().BeOnOrBefore(after.AddSeconds(1));

        source.LastRunAt.Should().NotBeNull();
        source.LastRunAt!.Value.Should().Be(job.ScheduledFor);

        source.NextRunAt.Should().NotBeNull("the first tick sets the fresh source's cadence");
        var nextRunAt = source.NextRunAt!.Value;
        nextRunAt.Should().BeOnOrAfter(before + TimeSpan.FromMinutes(IntervalMinutes) * 0.9 - TimeSpan.FromSeconds(1));
        nextRunAt.Should().BeOnOrBefore(after + TimeSpan.FromMinutes(IntervalMinutes) * 1.1 + TimeSpan.FromSeconds(1));

        var items = await DrainAsync(queue);
        items.Should().Contain(i => i.JobId == job.Id && i.SourceId == sourceId);
    }
}
