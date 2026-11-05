using System.Net;
using System.Security.Cryptography;
using EmailAgent.Core.Audit;
using EmailAgent.Core.Errors;
using EmailAgent.Core.Identity;
using EmailAgent.Core.Options;
using EmailAgent.Infrastructure.Audit;
using EmailAgent.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EmailAgent.Infrastructure.Identity;

/// <summary>
/// EF Core <see cref="IRefreshTokenStore"/> (M1T4 task step 3): issuance,
/// hash lookup, atomic rotation with reuse detection and family/single
/// revocation against the <c>refresh_tokens</c> table (SQ-1).
/// </summary>
/// <remarks>
/// <para>
/// Rotation runs in a single Postgres transaction. "One winner" under
/// contention comes from a <b>conditional consume</b> (UPDATE … SET used_at
/// WHERE id = … AND used_at IS NULL AND revoked_at IS NULL): a concurrent
/// loser's UPDATE re-evaluates that predicate against the winner's committed
/// row and matches 0 rows, so it takes the reuse path. The unique
/// <c>token_hash</c> index alone does NOT provide this — it guards hashes,
/// not consume-once (§13.11 / Q-API5 / task step 3).
/// </para>
/// <para>
/// Reuse: if the row is already used or revoked — or the conditional consume
/// loses a race — the whole <c>family_id</c> partition is revoked, a
/// <c>refresh_reuse_detected</c> audit row is written (in the same
/// transaction — the store never silently drops the evidence) and
/// <see cref="RefreshTokenReuseException"/> is thrown for M1T6 to map to 401.
/// An audit-write failure surfaces as-is: losing the evidence is worse than
/// failing the request.
/// </para>
/// <para>
/// Expiry: a token past <c>expires_at</c> is refused with
/// <see cref="RefreshTokenExpiredException"/> before any consume step — the
/// TTL has server-side teeth regardless of what the cookie claims.
/// </para>
/// </remarks>
public sealed class RefreshTokenStore : IRefreshTokenStore
{
    /// <summary>SQ-9 / Q-A10: 256-bit raw token, stored as SHA-256.</summary>
    private const int RawTokenByteLength = 32;

    private readonly EmailAgentDbContext _db;
    private readonly IAuditLogger _audit;
    private readonly TimeSpan _ttl;

    public RefreshTokenStore(
        EmailAgentDbContext db,
        IAuditLogger audit,
        IOptions<AuditOptions> auditOptions,
        IOptions<AuthOptions> authOptions)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _ = auditOptions ?? throw new ArgumentNullException(nameof(auditOptions));
        _ttl = authOptions?.Value?.RefreshTokenTtl ?? TimeSpan.FromDays(14);
    }

    public async Task<IssuedRefreshToken> IssueAsync(
        Guid userId,
        string userAgent,
        string ip,
        CancellationToken ct)
    {
        var raw = RandomNumberGenerator.GetBytes(RawTokenByteLength);
        var rawToken = Convert.ToBase64String(raw);
        var tokenHash = SHA256.HashData(raw);
        var now = DateTimeOffset.UtcNow;

        var entity = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FamilyId = Guid.NewGuid(),
            TokenHash = tokenHash,
            IssuedAt = now,
            ExpiresAt = now + _ttl,
            UserAgent = userAgent,
            Ip = ParseIp(ip),
        };

        // Task step 3: issuance runs in an explicit transaction (a single
        // INSERT is already atomic, but the step asks for the transaction —
        // and it keeps Issue/Rotate symmetrical for future multi-row writes).
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        _db.RefreshTokens.Add(entity);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new IssuedRefreshToken(ToRecord(entity), rawToken);
    }

    public async Task<RefreshTokenRecord?> FindByHashAsync(byte[] tokenHash, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);

        var entity = await _db.RefreshTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.TokenHash == tokenHash, ct);

        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IssuedRefreshToken> RotateAsync(RefreshTokenRecord old, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(old);

        // Task step 3: atomic rotation — the consume step and the successor
        // insert commit together (§13.11 / Q-API5).
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        // Untracked read: reuse/expiry decisions must rest on committed DB
        // truth, never on whatever this context happens to have tracked
        // (revocations bypass the change tracker via ExecuteUpdate).
        var entity = await _db.RefreshTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == old.Id, ct)
            ?? throw new InvalidOperationException(
                $"Refresh token {old.Id} disappeared between lookup and rotation.");

        if (entity.UsedAt is not null || entity.RevokedAt is not null)
        {
            await ReuseDetectedAsync(entity, ct);
            await transaction.CommitAsync(ct);
            throw new RefreshTokenReuseException();
        }

        var now = DateTimeOffset.UtcNow;

        if (entity.ExpiresAt <= now)
        {
            // Server-side TTL enforcement (AUTH__RefreshTokenTtl): expiry is
            // not reuse — no family revocation, no audit row — but the token
            // must never silently extend the session.
            await transaction.CommitAsync(ct);
            throw new RefreshTokenExpiredException(entity.Id, entity.ExpiresAt);
        }

        var raw = RandomNumberGenerator.GetBytes(RawTokenByteLength);
        var rawToken = Convert.ToBase64String(raw);
        var successorId = Guid.NewGuid();

        // The single-winner step: conditional consume. Under READ COMMITTED a
        // concurrent loser waits on the row lock and, once the winner commits,
        // re-evaluates this predicate against the new row version — 0 rows
        // affected means someone else consumed the token first.
        var consumed = await _db.RefreshTokens
            .Where(r => r.Id == entity.Id && r.UsedAt == null && r.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.UsedAt, now), ct);

        if (consumed == 0)
        {
            // Two presenters of one token: that is reuse by definition —
            // revoke the family and emit the evidence row.
            await ReuseDetectedAsync(entity, ct);
            await transaction.CommitAsync(ct);
            throw new RefreshTokenReuseException();
        }

        var successor = new RefreshToken
        {
            Id = successorId,
            UserId = entity.UserId,
            FamilyId = entity.FamilyId,
            TokenHash = SHA256.HashData(raw),
            IssuedAt = now,
            ExpiresAt = now + _ttl,
            UserAgent = entity.UserAgent,
            Ip = entity.Ip,
        };

        // Insert before linking: replaced_by_id is a self-referencing FK
        // (ON DELETE RESTRICT), so the successor row must exist when the
        // UPDATE references it. All three statements commit atomically.
        _db.RefreshTokens.Add(successor);
        await _db.SaveChangesAsync(ct);

        await _db.RefreshTokens
            .Where(r => r.Id == entity.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReplacedById, successorId), ct);

        await transaction.CommitAsync(ct);

        return new IssuedRefreshToken(ToRecord(successor), rawToken);
    }

    public async Task RevokeFamilyAsync(Guid familyId, CancellationToken ct)
    {
        await _db.RefreshTokens
            .Where(r => r.FamilyId == familyId && r.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(r => r.RevokedAt, DateTimeOffset.UtcNow),
                ct);
    }

    public async Task RevokeAsync(byte[] tokenHash, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);

        await _db.RefreshTokens
            .Where(r => r.TokenHash == tokenHash && r.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(r => r.RevokedAt, DateTimeOffset.UtcNow),
                ct);
    }

    private async Task ReuseDetectedAsync(RefreshToken entity, CancellationToken ct)
    {
        await RevokeFamilyAsync(entity.FamilyId, ct);

        var user = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == entity.UserId, ct);

        await _audit.LogAsync(
            new AuditEvent(
                ActorUserId: user?.Id ?? entity.UserId,
                ActorRole: user?.Role.ToString(),
                Action: AuditActions.RefreshReuseDetected,
                TargetType: "refresh_token",
                TargetId: entity.Id.ToString(),
                MetadataJson: $"{{\"family_id\":\"{entity.FamilyId}\",\"user_id\":\"{entity.UserId}\"}}",
                Ip: entity.Ip),
            ct);
    }

    private static RefreshTokenRecord ToRecord(RefreshToken entity) => new(
        entity.Id,
        entity.UserId,
        entity.FamilyId,
        entity.TokenHash,
        entity.IssuedAt,
        entity.ExpiresAt,
        entity.UsedAt,
        entity.RevokedAt,
        entity.ReplacedById,
        entity.UserAgent,
        entity.Ip);

    private static IPAddress? ParseIp(string? ip)
        => string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip, out var parsed)
            ? null
            : parsed;
}