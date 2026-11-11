namespace EmailAgent.Core.Security;

/// <summary>
/// The status of a crypto-session lookup (M5T1, SQ-M5-1). Distinguishing
/// every failure mode is what lets the pipeline (M5T3/M5T5) treat a locked
/// session as the defined transient outcome of SQ-M5-10.
/// </summary>
public enum CryptoSessionStatus
{
    /// <summary>The lookup succeeded and the DEK is available.</summary>
    Ok,

    /// <summary>The user has no crypto session — the SPA must prompt for the passphrase.</summary>
    Locked,

    /// <summary>The session exists but its idle or absolute timeout has elapsed.</summary>
    Expired,

    /// <summary>
    /// A token was presented but it does not authenticate any live session —
    /// wrong, malformed, or revoked (stale tab: re-unlock).
    /// </summary>
    Absent,
}

/// <summary>
/// Result of a crypto-session lookup (M5T1). <see cref="Dek"/> and
/// <see cref="SessionId"/> are set only when <see cref="Status"/> is
/// <see cref="CryptoSessionStatus.Ok"/>; the DEK is a fresh copy the caller
/// owns and must zero when done.
/// </summary>
public readonly record struct CryptoSessionResult(
    CryptoSessionStatus Status,
    byte[]? Dek,
    Guid? SessionId)
{
    /// <summary>The session is valid and the DEK may be used.</summary>
    public bool IsOk => Status == CryptoSessionStatus.Ok;

    /// <summary>Successful lookup carrying the unwrapped DEK and the session id.</summary>
    public static CryptoSessionResult Ok(byte[] dek, Guid sessionId) =>
        new(CryptoSessionStatus.Ok, dek, sessionId);

    /// <summary>No session exists for the user.</summary>
    public static CryptoSessionResult Locked() =>
        new(CryptoSessionStatus.Locked, null, null);

    /// <summary>The session's idle or absolute timeout elapsed.</summary>
    public static CryptoSessionResult Expired() =>
        new(CryptoSessionStatus.Expired, null, null);

    /// <summary>The presented token did not authenticate any live session.</summary>
    public static CryptoSessionResult Absent() =>
        new(CryptoSessionStatus.Absent, null, null);
}

/// <summary>
/// The one-time handle returned by <see cref="ICryptoSessionStore.CreateOrReplace"/>:
/// the session id plus the raw session-key token (base64url of the 256-bit
/// session key). The token is shown once — the store keeps only its SHA-256
/// hash (M5T1, SQ-M5-1).
/// </summary>
public sealed record CryptoSessionHandle(Guid SessionId, string Token);

/// <summary>
/// The in-process crypto-session entry (M5T1, SQ-M5-1): exactly the fields
/// the store holds in <c>IMemoryCache</c> — no database entity, no table, no
/// migration. <see cref="TokenHash"/> is <c>sha256(SK-token)</c> (UTF-8 of the
/// base64url token); <see cref="DekWrappedBySk"/> is the DEK AES-GCM-wrapped
/// under the session key (ciphertext + tag with its 12-byte nonce prefix).
/// <see cref="SessionKey"/> is the raw 256-bit session key the entry needs to
/// unwrap the DEK for the user-keyed worker lookup
/// (<see cref="ICryptoSessionStore.TryGetDekForUser"/>), which has no
/// presented token to authenticate; it, like everything else here, lives only
/// in process RAM for the life of the session and dies with the process.
/// </summary>
public sealed record CryptoSession(
    Guid SessionId,
    Guid UserId,
    string TokenHash,
    byte[] DekWrappedBySk,
    byte[] SessionKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset IdleExpiresAt,
    DateTimeOffset AbsoluteExpiresAt);
