using EmailAgent.Infrastructure.Enums;

namespace EmailAgent.Infrastructure.Entities;

/// <summary>§6 <c>scheduled_jobs</c> row (§10 job lifecycle, M5).</summary>
public class ScheduledJob
{
    public Guid Id { get; set; }

    public Guid SourceId { get; set; }

    public DateTimeOffset ScheduledFor { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public ScheduledJobStatus Status { get; set; }

    public string? Error { get; set; }

    public int Attempt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public MessageSource Source { get; set; } = null!;
}