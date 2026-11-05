namespace EmailAgent.Core.Identity;

/// <summary>
/// One-time password service (Q-A2 / Q-A3, §8): 16-char URL-safe base32
/// OTPs with a 7-day default lifetime, single-use, hashed at rest
/// (PBKDF2-SHA256 per SQ-5 / Q-A8), regenerable by an admin (which revokes
/// prior unused OTPs — SQ-6 / Q-A9). EF Core implementation lives in
/// <c>EmailAgent.Infrastructure</c>; the M1T6 otp-login endpoint and the
/// M1T7 admin issue/reissue endpoints are the consumers.
/// </summary>
public interface IOtpService
{
    /// <summary>
    /// Issues a fresh OTP for <paramref name="userId"/>: generates 16 chars
    /// from the base32 alphabet (80 bits), stores only the PBKDF2-SHA256
    /// <c>salt || hash</c>, sets <c>expires_at = now + OTP__Lifetime</c> and
    /// records <paramref name="createdByAdminId"/> as the issuer. Returns the
    /// raw OTP exactly once — the only path that ever yields it.
    /// </summary>
    Task<IssuedOtp> IssueAsync(Guid userId, Guid createdByAdminId, CancellationToken ct);

    /// <summary>
    /// Single-use verifier. Considers only the most recent still-active row
    /// (unused, unexpired, not revoked). Returns <c>true</c> on the first
    /// matching candidate and marks <c>used_at</c> atomically (a concurrent
    /// second presenter loses the conditional consume and gets <c>false</c>);
    /// returns <c>false</c> on mismatch, expiry, prior use, or revocation.
    /// </summary>
    Task<bool> VerifyAsync(Guid userId, string candidateOtp, CancellationToken ct);

    /// <summary>
    /// Admin reissue (Q-A9): revokes every unused, unexpired OTP for the
    /// user, then issues a fresh one in the same transaction — at most one
    /// active OTP per user afterwards. The raw OTP is returned exactly once,
    /// like <see cref="IssueAsync"/>.
    /// </summary>
    Task<IssuedOtp> RegenerateAsync(Guid userId, Guid createdByAdminId, CancellationToken ct);

    /// <summary>
    /// Revokes every unused, unexpired OTP for the user (sets
    /// <c>revoked_at</c>); returns how many rows were newly revoked. No M1
    /// endpoint calls this yet — delete-account (Q-LP2) lands in M6a/M7a —
    /// but the service method is live so that flow can adopt it unchanged.
    /// </summary>
    Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken ct);
}
