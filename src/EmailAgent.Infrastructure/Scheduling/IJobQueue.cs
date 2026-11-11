namespace EmailAgent.Infrastructure.Scheduling;

/// <summary>
/// The bounded job-channel contract between the M5T2 scheduler (producer)
/// and the M5T3 worker pool (consumers) — §4's bounded
/// <c>Channel&lt;JobItem&gt;</c> seam. One <b>singleton</b> instance is
/// registered and shared by both sides so they meet on the same channel.
/// </summary>
public interface IJobQueue
{
    /// <summary>
    /// Producer side: awaits room when the channel is at capacity
    /// (<c>FullMode = Wait</c>) — back-pressure, never a dropped job.
    /// Throws <see cref="System.Threading.Channels.ChannelClosedException"/>
    /// once <see cref="Complete"/> has run.
    /// </summary>
    ValueTask EnqueueAsync(JobItem item, CancellationToken cancellationToken);

    /// <summary>
    /// Consumer side: yields the buffered items and ends when
    /// <see cref="Complete"/> has been called and the channel is drained —
    /// this is how M5T3 observes the §4 graceful-shutdown hand-off.
    /// </summary>
    IAsyncEnumerable<JobItem> ReadAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Approximate number of buffered (not yet consumed) items — feeds the
    /// §15 <c>email_agent_queue_depth</c> gauge M5T3 emits.
    /// </summary>
    int ApproximateDepth { get; }

    /// <summary>
    /// Marks the producer side finished: blocked writers/readers are released,
    /// <see cref="ReadAllAsync"/> drains the remainder and then observes
    /// completion (§4 graceful shutdown: stop scheduler, drain pool).
    /// </summary>
    void Complete();
}
