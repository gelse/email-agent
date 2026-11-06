namespace EmailAgent.Infrastructure.Enums;

/// <summary>
/// §6 <c>scheduled_jobs.status</c>: enum(<c>Pending</c>, <c>Running</c>,
/// <c>Succeeded</c>, <c>Failed</c>, <c>DeadLettered</c>) — stored as text per
/// M1T2 task step 9 (no <c>HasPostgresEnum</c>).
/// </summary>
public enum ScheduledJobStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    DeadLettered,
}
