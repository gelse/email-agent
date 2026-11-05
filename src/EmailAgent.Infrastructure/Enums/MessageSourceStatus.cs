namespace EmailAgent.Infrastructure.Enums;

/// <summary>
/// §6 <c>message_sources.status</c>: enum(<c>Active</c>, <c>Paused</c>,
/// <c>Error</c>) — stored as text per M1T2 task step 9 (no
/// <c>HasPostgresEnum</c>).
/// </summary>
public enum MessageSourceStatus
{
    Active,
    Paused,
    Error,
}