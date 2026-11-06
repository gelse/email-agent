namespace EmailAgent.Core.Identity;

/// <summary>
/// Rotating refresh-token store (M1 SQ-1 / Q-API5, §13.11): issuance,
/// hash lookup, atomic rotation with reuse detection, and family/single
/// revocation. EF Core implementation lives in
/// <c>EmailAgent.Infrastructure</c>; the endpoint wiring is M1T6's.
/// </summary>
public interface IRefreshTokenStore
{
    /// <summary>
    /// Issues a fresh token family for <paramref name="userId"/>: 32 random
    /// raw bytes (SHA-256 hashed for storage), <c>family_id = new Guid()</c>,
    /// <c>expires_at = now + AUTH__RefreshTokenTtl</c>. Returns the persisted
    /// record and the raw token.
    /// </summary>
    Task<IssuedRefreshToken> IssueAsync(Guid userId, string userAgent, string ip, CancellationToken ct);

    /// <summary>Looks the token up by SHA-256 hash; <c>null</c> on miss.</summary>
    Task<RefreshTokenRecord?> FindByHashAsync(byte[] tokenHash, CancellationToken ct);

    /// <summary>
    /// Atomically marks <paramref name="old"/> as used and inserts its
    /// successor in the same family; the conditional consume (only when the
    /// row is still live) elects a single winner under contention. If the old
    /// row was already used or revoked — or a concurrent presenter wins the
    /// race — treats it as reuse: revokes the whole family, writes the
    /// <c>refresh_reuse_detected</c> audit row and throws
    /// <see cref="Errors.RefreshTokenReuseException"/>. A token past
    /// <c>expires_at</c> is refused with
    /// <see cref="Errors.RefreshTokenExpiredException"/> before any consume
    /// (no revocation, no reuse row); M1T6 maps both to RFC 7807 401.
    /// </summary>
    Task<IssuedRefreshToken> RotateAsync(RefreshTokenRecord old, CancellationToken ct);

    /// <summary>Revokes every still-live token in the family.</summary>
    Task RevokeFamilyAsync(Guid familyId, CancellationToken ct);

    /// <summary>Revokes the single token (logout).</summary>
    Task RevokeAsync(byte[] tokenHash, CancellationToken ct);
}
