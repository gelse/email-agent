namespace EmailAgent.Core.Security;

/// <summary>
/// Request-scoped view of the caller's crypto session (M5T1). Every
/// user-data handler resolves the DEK through this seam instead of reading a
/// request header, and <see cref="CryptoSessionMiddleware"/> uses it as the
/// generic 401-vs-403 enforcement point for the whole <c>/v1/user/*</c>
/// namespace (SQ-M5-1).
/// </summary>
public interface ICryptoSessionAccessor
{
    /// <summary>Whether the current request carries a valid, unexpired crypto session.</summary>
    bool IsUnlocked { get; }

    /// <summary>
    /// The caller's unwrapped DEK (a fresh copy the caller must zero) plus the
    /// session id, or a non-<see cref="CryptoSessionStatus.Ok"/> result the
    /// caller distinguishes from "no session".
    /// </summary>
    CryptoSessionResult TryGetDek();

    /// <summary>
    /// Sliding idle touch for the current session — called on every
    /// successful user-data request so activity refreshes the idle timer.
    /// </summary>
    void Touch();
}
