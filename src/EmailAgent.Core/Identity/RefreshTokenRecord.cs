using System.Net;

namespace EmailAgent.Core.Identity;

/// <summary>
/// Server-side view of a <c>refresh_tokens</c> row (SQ-1 / SQ-9 / Q-API5).
/// Mirrors <c>plans/01-architecture.md</c> §6; the raw token is never part of
/// this record — only its SHA-256 hash ever reaches storage.
/// </summary>
public sealed record RefreshTokenRecord(
    Guid Id,
    Guid UserId,
    Guid FamilyId,
    byte[] TokenHash,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? UsedAt,
    DateTimeOffset? RevokedAt,
    Guid? ReplacedById,
    string? UserAgent,
    IPAddress? Ip);
