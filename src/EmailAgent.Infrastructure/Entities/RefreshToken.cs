using System.Net;

namespace EmailAgent.Infrastructure.Entities;

/// <summary>
/// §6 <c>refresh_tokens</c> row (M1 SQ-1 / Q-A6): server-side store for
/// rotating refresh tokens — one <c>family_id</c> per login, reuse revokes
/// the whole family (§13.11 / Q-API5). The raw token is never stored;
/// <c>token_hash</c> is SHA-256 of the opaque token (Q-A10 / SQ-9).
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>One family per login; reuse revokes the whole family.</summary>
    public Guid FamilyId { get; set; }

    /// <summary>SHA-256 of the opaque refresh token (32 bytes).</summary>
    public byte[] TokenHash { get; set; } = null!;

    public DateTimeOffset IssuedAt { get; set; }

    /// <summary><c>AUTH__RefreshTokenTtl</c>, default 14 days.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set when the row is consumed by rotation.</summary>
    public DateTimeOffset? UsedAt { get; set; }

    /// <summary>Set on logout or family-wide revocation.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Self-FK: the rotation chain (old → new); Restrict so the chain head is never cascade-deleted.</summary>
    public Guid? ReplacedById { get; set; }

    public string? UserAgent { get; set; }

    /// <summary><c>inet</c> — client address at issuance.</summary>
    public IPAddress? Ip { get; set; }

    public User User { get; set; } = null!;

    public RefreshToken? ReplacedBy { get; set; }
}