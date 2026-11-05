namespace EmailAgent.Infrastructure.Enums;

/// <summary>
/// §6 <c>pending_actions.status</c>: enum(<c>Pending</c>, <c>Approved</c>,
/// <c>Rejected</c>, <c>Executed</c>, <c>Expired</c>) — stored as text with
/// <c>DEFAULT 'Pending'</c> per M1T2 task step 9.
/// </summary>
public enum PendingActionStatus
{
    Pending,
    Approved,
    Rejected,
    Executed,
    Expired,
}