namespace EmailAgent.Core.Security;

/// <summary>
/// In-process store for crypto sessions (M5T1, SQ-M5-1). At most one live
/// session per user; the store holds the DEK only AES-GCM-wrapped under a
/// random 256-bit session key, and the token only as its SHA-256 hash — there
/// is deliberately no <c>EmailAgentDbContext</c> dependency and no persistence
/// path, so a database dump can never contain a DEK in the clear (the durable
/// custody form stays <c>users.dek_wrapped</c>, wrapped under the
/// passphrase-derived KEK the server never sees).
/// </summary>
/// <remarks>
/// The store is per-process (in-memory): a server restart invalidates every
/// session and users must re-unlock, and a multi-instance deployment would
/// need sticky sessions or a shared encrypted store (out of v1 scope — the
/// Backend ships as a single container, §4).
/// </remarks>
public interface ICryptoSessionStore
{
    /// <summary>
    /// Creates the user's session, revoking any existing one first (idempotent
    /// replace, SQ-M5-1): generates a fresh 256-bit session key, wraps
    /// <paramref name="dek"/> under it, and returns the handle whose
    /// <see cref="CryptoSessionHandle.Token"/> must be shown to the client
    /// exactly once — the store itself retains only its hash.
    /// </summary>
    /// <param name="userId">The session owner (one live session per user).</param>
    /// <param name="dek">The raw 32-byte data-encryption key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    CryptoSessionHandle CreateOrReplace(Guid userId, ReadOnlyMemory<byte> dek, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a presented token for a user: <see cref="CryptoSessionStatus.Ok"/>
    /// with the unwrapped DEK, <see cref="CryptoSessionStatus.Expired"/> when a
    /// timeout elapsed, or <see cref="CryptoSessionStatus.Absent"/> when the
    /// token is missing, malformed, or does not authenticate any live session
    /// (a revoked or unknown token). A user with no session at all surfaces as
    /// <see cref="CryptoSessionStatus.Locked"/> through
    /// <see cref="TryGetDekForUser"/> and the request accessor.
    /// </summary>
    CryptoSessionResult TryGetDek(Guid userId, string sessionToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// User-keyed lookup with no presented token — the worker pool has no HTTP
    /// request, so job activity resolves the DEK by user id (M5T1 exposes the
    /// result; M5T3/M5T5 own the <c>error='crypto-locked'</c> retry policy,
    /// SQ-M5-10).
    /// </summary>
    CryptoSessionResult TryGetDekForUser(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sliding idle touch: resets <c>last_seen_at</c> and the idle deadline to
    /// now + <c>SESSION__IdleTimeout</c>, never past the absolute deadline.
    /// Returns <c>false</c> when the session is missing or already expired.
    /// </summary>
    bool Touch(Guid userId, string sessionToken);

    /// <summary>User-keyed sliding idle touch (job activity on the user's behalf, SQ-M5-10).</summary>
    bool TouchForUser(Guid userId);

    /// <summary>
    /// Revokes the user's session (logout, unlock replacement, refresh-family
    /// revocation, delete-user cascade) and returns the revoked session id for
    /// the caller's audit row, or <c>null</c> when nothing was live.
    /// </summary>
    Guid? RevokeByUser(Guid userId);

    /// <summary>Revokes one session by id; returns whether it was live.</summary>
    bool RevokeBySessionId(Guid sessionId);
}
