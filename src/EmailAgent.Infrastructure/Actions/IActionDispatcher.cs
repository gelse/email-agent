namespace EmailAgent.Infrastructure.Actions;

/// <summary>
/// M4T1: the §12 action dispatcher — validates, (optionally) confirms or
/// dry-runs, and executes engine <c>tool_call</c>s for one processed message.
/// </summary>
public interface IActionDispatcher
{
    Task<DispatchResult> DispatchAsync(DispatchRequest request, CancellationToken ct);
}
