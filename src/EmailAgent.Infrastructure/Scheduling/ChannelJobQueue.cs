using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace EmailAgent.Infrastructure.Scheduling;

/// <summary>
/// M5T2's bounded <see cref="Channel{T}"/>-backed <see cref="IJobQueue"/>
/// (§4): capacity from <c>WORKERS__ChannelCapacity</c>
/// (<see cref="JobQueueOptions.ChannelCapacity"/>, DEFAULT 256),
/// <see cref="BoundedChannelFullMode.Wait"/> so a full channel
/// back-pressures the scheduler instead of dropping jobs, and a
/// <c>SingleReader = false</c> reader side because M5T3 runs N concurrent
/// consumers.
/// </summary>
/// <remarks>
/// <c>SingleWriter = true</c> is set per the M5T2 task file's channel shape.
/// The scheduler's independent per-source units may reach
/// <see cref="EnqueueAsync"/> concurrently; that stays safe because
/// <c>BoundedChannel&lt;T&gt;</c>'s writer synchronizes on its internal gate
/// unconditionally (verified against the dotnet/runtime implementation — the
/// bounded channel does not take a lock-free single-writer fast path).
/// </remarks>
public sealed class ChannelJobQueue : IJobQueue
{
    private readonly Channel<JobItem> _channel;

    public ChannelJobQueue(IOptions<JobQueueOptions> options)
    {
        var capacity = options.Value.ChannelCapacity;
        _channel = Channel.CreateBounded<JobItem>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
        });
    }

    /// <inheritdoc />
    public ValueTask EnqueueAsync(JobItem item, CancellationToken cancellationToken)
        => _channel.Writer.WriteAsync(item, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<JobItem> ReadAllAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);

    /// <inheritdoc />
    public int ApproximateDepth => _channel.Reader.CanCount ? _channel.Reader.Count : 0;

    /// <inheritdoc />
    public void Complete() => _channel.Writer.TryComplete();
}
