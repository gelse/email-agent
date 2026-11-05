namespace EmailAgent.Core.Audit;

/// <summary>
/// Append-only audit-trail writer (M1T4 task step 4). Implementations must
/// serialize concurrent appends so the HMAC chain can never fork.
/// </summary>
public interface IAuditLogger
{
    /// <summary>
    /// Computes the row HMAC over the previous row's HMAC (genesis for the
    /// first row), inserts the row and commits. Audit writes are best-effort
    /// from the caller's perspective only insofar as the caller chooses to
    /// await them — they are not swallowed here.
    /// </summary>
    Task LogAsync(AuditEvent evt, CancellationToken ct);

    /// <summary>
    /// Walks the chain in id order and returns the id of the first row whose
    /// recomputed HMAC does not match, or <c>null</c> when the chain is intact.
    /// </summary>
    Task<long?> VerifyChainAsync(CancellationToken ct);
}