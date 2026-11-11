using System.Threading.Channels;
using EmailAgent.Infrastructure.Scheduling;
using Microsoft.Extensions.Options;
using Xunit;

namespace EmailAgent.UnitTests.Scheduling;

/// <summary>
/// Unit tests for <see cref="ChannelJobQueue"/> (M5T2 task file, "Tests"):
/// <c>Enqueue_BeyondCapacity_Awaits</c>, <c>Complete_SignalsReaders</c>,
/// <c>ApproximateDepth_ReflectsPendingCount</c>.
/// </summary>
public class ChannelJobQueueTests
{
    private static ChannelJobQueue CreateQueue(int capacity = 4)
        => new(Options.Create(new JobQueueOptions { ChannelCapacity = capacity }));

    [Fact]
    public async Task Enqueue_BeyondCapacity_Awaits()
    {
        var queue = CreateQueue(capacity: 1);
        var sourceId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        // Fill the single slot: the writer accepts immediately.
        await queue.EnqueueAsync(new JobItem(Guid.NewGuid(), sourceId, userId), CancellationToken.None);

        // Channel full: the second enqueue must await rather than throw or drop
        // (FullMode = Wait back-pressure, §4).
        var blocked = queue.EnqueueAsync(new JobItem(Guid.NewGuid(), sourceId, userId), CancellationToken.None);
        Assert.False(blocked.IsCompleted, "a full channel must back-pressure the producer, not complete synchronously");

        // Draining the buffered item releases the waiting writer.
        await foreach (var _ in queue.ReadAllAsync(CancellationToken.None))
        {
            break; // consumed the first item; the pending enqueue can now complete
        }

        await blocked.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, queue.ApproximateDepth);
    }

    [Fact]
    public async Task Complete_SignalsReaders()
    {
        var queue = CreateQueue();
        var buffered = new JobItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await queue.EnqueueAsync(buffered, CancellationToken.None);

        queue.Complete();

        // A producer arriving after the §4 hand-off observes closure instead of
        // silently losing the job (IJobQueue.EnqueueAsync contract).
        await Assert.ThrowsAsync<ChannelClosedException>(async () =>
            await queue.EnqueueAsync(new JobItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None));

        // The reader drains the buffered remainder and then the enumeration
        // ENDS — this is how M5T3 observes graceful shutdown.
        var seen = 0;
        await foreach (var _ in queue.ReadAllAsync(CancellationToken.None))
        {
            seen++;
        }

        Assert.Equal(1, seen);
    }

    [Fact]
    public async Task ApproximateDepth_ReflectsPendingCount()
    {
        var queue = CreateQueue(capacity: 4);
        Assert.Equal(0, queue.ApproximateDepth);

        var firstJobId = Guid.NewGuid();
        await queue.EnqueueAsync(new JobItem(firstJobId, Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
        await queue.EnqueueAsync(new JobItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
        Assert.Equal(2, queue.ApproximateDepth);

        // FIFO: consuming the head leaves exactly the second item buffered.
        await using (var enumerator = queue.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator(CancellationToken.None))
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(firstJobId, enumerator.Current.JobId);
        }

        Assert.Equal(1, queue.ApproximateDepth);
    }
}
