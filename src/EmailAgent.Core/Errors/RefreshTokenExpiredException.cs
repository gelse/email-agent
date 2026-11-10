namespace EmailAgent.Core.Errors;

/// <summary>
/// The presented refresh token is past <c>expires_at</c> — the server-side
/// enforcement of <c>AUTH__RefreshTokenTtl</c> (14-day default). Deliberately
/// distinct from <see cref="RefreshTokenReuseException"/>: expiry is normal
/// end-of-life, not evidence of theft, so the family is NOT revoked and no
/// <c>refresh_reuse_detected</c> audit row is written. M1T6 maps this type to
/// RFC 7807 401 (client must re-authenticate).
/// </summary>
public sealed class RefreshTokenExpiredException(Guid tokenId, DateTimeOffset expiresAt)
    : Exception($"Refresh token {tokenId} expired at {expiresAt:O}; re-authentication is required.")
{
    /// <summary>The refresh token that was refused.</summary>
    public Guid TokenId { get; } = tokenId;

    /// <summary>The instant the token stopped being renewable.</summary>
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
}
