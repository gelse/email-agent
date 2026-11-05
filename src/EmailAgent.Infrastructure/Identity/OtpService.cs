using System.Security.Cryptography;
using EmailAgent.Core.Identity;
using EmailAgent.Infrastructure.Entities;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmailAgent.Infrastructure.Identity;

/// <summary>
/// EF Core <see cref="IOtpService"/> (M1T5): 16-char URL-safe base32 OTPs
/// (Q-A2), PBKDF2-SHA256 hashing at rest (SQ-5 / Q-A8), single-use consume,
/// expiry, and admin reissue that revokes prior unused rows (SQ-6 / Q-A9).
/// </summary>
/// <remarks>
/// <para>
/// Single-use under contention: <see cref="VerifyAsync"/> reads the most
/// recent active row, verifies its PBKDF2 hash, then conditional-consumes
/// (<c>UPDATE … SET used_at WHERE id = … AND used_at IS NULL AND
/// revoked_at IS NULL AND expires_at &gt; now</c>) inside one
/// transaction. Under READ COMMITTED a concurrent loser re-evaluates that
/// predicate against the winner's committed row and matches 0 rows, so at
/// most one call returns true — the same pattern as
/// <see cref="RefreshTokenStore"/>'s rotation, with the revocation guard
/// repeated because the PBKDF2 verify opens a window before the consume.
/// </para>
/// <para>
/// Single-active invariant: <see cref="RegenerateAsync"/> takes a
/// transaction-scoped <c>FOR UPDATE</c> lock on the user's row (so
/// concurrent reissues serialize even when the revoke below locks nothing)
/// and revokes every unused, unexpired row before inserting the successor,
/// so at most one active OTP exists per user. The "most recent active"
/// lookup is the documented fallback if a future write path bypasses
/// Regenerate (task Risks — most-recent-unused semantics).
/// </para>
/// <para>
/// The raw OTP leaves the process exactly once — in the
/// <see cref="IssuedOtp"/> returned by Issue/Regenerate. The row stores only
/// the 48-byte <c>salt || hash</c> (Q-A2 view-once; the HTTP layer's
/// view-once enforcement is M1T7/M1T8). Never log the raw value.
/// </para>
/// </remarks>
public sealed class OtpService : IOtpService
{
    /// <summary>Q-A2: 16 chars × 5 bits = 80 bits of entropy.</summary>
    private const int RawOtpCharLength = 16;

    /// <summary>10 random bytes base32-encode to exactly 16 chars (80 bits).</summary>
    private const int RawOtpByteLength = 10;

    /// <summary>RFC 4648 §6 base32 — the URL-safe alphabet Q-A2 names.</summary>
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    // SQ-5 / Q-A8: PBKDF2-SHA256, per-OTP 16-byte salt, 100 000 iterations,
    // 32-byte digest; stored as salt || hash (48 bytes) in otp_hash bytea.
    private const int OtpSaltByteLength = 16;
    private const int OtpHashByteLength = 32;
    private const int OtpHashIterations = 100_000;
    private const int StoredHashByteLength = OtpSaltByteLength + OtpHashByteLength;

    private readonly EmailAgentDbContext _db;
    private readonly ILogger<OtpService> _logger;
    private readonly TimeSpan _lifetime;

    public OtpService(
        EmailAgentDbContext db,
        IOptions<OtpOptions> options,
        ILogger<OtpService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ = options ?? throw new ArgumentNullException(nameof(options));
        _lifetime = options.Value.Lifetime;
    }

    public async Task<IssuedOtp> IssueAsync(Guid userId, Guid createdByAdminId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var rawOtp = GenerateRawOtp();
        var entity = new OneTimePassword
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            OtpHash = HashOtp(rawOtp),
            ExpiresAt = now + _lifetime,
            UsedAt = null,
            RevokedAt = null,
            CreatedBy = createdByAdminId,
            CreatedAt = now,
        };

        // Explicit transaction per the M1T5 task step (symmetrical with
        // Verify/Regenerate; a lone INSERT is already atomic).
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        _db.OneTimePasswords.Add(entity);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        _logger.LogInformation(
            "OTP {OtpId} issued for user {UserId} by admin {AdminId}; expires {ExpiresAt}.",
            entity.Id, entity.UserId, createdByAdminId, entity.ExpiresAt);

        return new IssuedOtp(entity.Id, entity.UserId, rawOtp, entity.ExpiresAt);
    }

    public async Task<bool> VerifyAsync(Guid userId, string candidateOtp, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidateOtp);

        // Shape gate before any PBKDF2 work: a wrong-length or non-base32
        // candidate can never match, so don't spend 100k iterations on it
        // (and don't let request size amplify hashing cost).
        if (candidateOtp.Length != RawOtpCharLength || !IsBase32(candidateOtp))
        {
            return false;
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        // Most-recent active row wins (the single-active invariant above).
        // Untracked: the consume step must rest on committed DB truth, never
        // on whatever this context happens to have tracked.
        var now = DateTimeOffset.UtcNow;
        var entity = await _db.OneTimePasswords
            .AsNoTracking()
            .Where(o => o.UserId == userId
                        && o.UsedAt == null
                        && o.RevokedAt == null
                        && o.ExpiresAt > now)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (entity is null || !VerifyHash(candidateOtp, entity.OtpHash))
        {
            // Missing row (expired/revoked/never issued) or hash mismatch.
            await transaction.CommitAsync(ct);
            return false;
        }

        // The single-winner step: conditional consume. A concurrent loser's
        // UPDATE re-evaluates this predicate against the winner's committed
        // row and matches 0 rows (READ COMMITTED). The predicate repeats the
        // revocation/expiry guards because the PBKDF2 verify above opened a
        // window: an admin's concurrent Regenerate/RevokeAll must leave a
        // revoked OTP unable to consume itself (review WARNING-1; mirrors
        // RefreshTokenStore's consume guard).
        var consumed = await _db.OneTimePasswords
            .Where(o => o.Id == entity.Id
                        && o.UsedAt == null
                        && o.RevokedAt == null
                        && o.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.UsedAt, now), ct);

        await transaction.CommitAsync(ct);
        return consumed == 1;
    }

    public async Task<IssuedOtp> RegenerateAsync(Guid userId, Guid createdByAdminId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var rawOtp = GenerateRawOtp();
        var entity = new OneTimePassword
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            OtpHash = HashOtp(rawOtp),
            ExpiresAt = now + _lifetime,
            UsedAt = null,
            RevokedAt = null,
            CreatedBy = createdByAdminId,
            CreatedAt = now,
        };

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        // Serialize per user before anything else (review WARNING-2). The
        // revoke UPDATE below only locks rows it actually matches, so two
        // concurrent regenerates with zero live rows (first issuance, or an
        // expired prior OTP) would both insert and leave two active rows.
        // Locking the users row for the transaction makes the revoke+insert
        // pair strictly one-at-a-time per user; the FK guarantees the row.
        await _db.Database.ExecuteSqlRawAsync(
            "SELECT id FROM users WHERE id = {0} FOR UPDATE",
            new object[] { userId },
            ct);

        // Q-A9: revoke every still-live OTP, then insert its successor —
        // both statements commit atomically so no observer sees zero or two
        // active OTPs for the user.
        await _db.OneTimePasswords
            .Where(o => o.UserId == userId
                        && o.UsedAt == null
                        && o.RevokedAt == null
                        && o.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.RevokedAt, now), ct);

        _db.OneTimePasswords.Add(entity);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        _logger.LogInformation(
            "OTP {OtpId} reissued for user {UserId} by admin {AdminId}; expires {ExpiresAt}.",
            entity.Id, entity.UserId, createdByAdminId, entity.ExpiresAt);

        return new IssuedOtp(entity.Id, entity.UserId, rawOtp, entity.ExpiresAt);
    }

    public async Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // Q-LP2 (delete-account, M6a/M7a) will call this; ExecuteUpdate's
        // return value is the count of newly revoked rows.
        return await _db.OneTimePasswords
            .Where(o => o.UserId == userId
                        && o.UsedAt == null
                        && o.RevokedAt == null
                        && o.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.RevokedAt, now), ct);
    }

    /// <summary>
    /// 10 cryptographically random bytes → unpadded RFC 4648 base32 →
    /// exactly <see cref="RawOtpCharLength"/> chars (80 bits, Q-A2). The
    /// alphabet is <c>A–Z2–7</c>; there is no '=' padding because 10 bytes
    /// is a whole number of 5-bit groups.
    /// </summary>
    internal static string GenerateRawOtp()
    {
        Span<byte> bytes = stackalloc byte[RawOtpByteLength];
        RandomNumberGenerator.Fill(bytes);
        return Base32Encode(bytes);
    }

    /// <summary>
    /// Candidate charset gate for <see cref="VerifyAsync"/>: every char must
    /// be in the base32 alphabet (uppercase <c>A–Z</c> or <c>2–7</c>) —
    /// anything else cannot be a real OTP. Runs before PBKDF2 so a
    /// wrong-shape candidate never costs hashing work.
    /// </summary>
    private static bool IsBase32(string candidate)
    {
        foreach (var c in candidate)
        {
            var isUppercaseLetter = c >= 'A' && c <= 'Z';
            var isBase32Digit = c >= '2' && c <= '7';

            if (!isUppercaseLetter && !isBase32Digit)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Unpadded RFC 4648 §6 base32 encoder. 10 input bytes (80 bits) encode
    /// to exactly 16 characters — no padding is ever emitted, so callers
    /// needing a fixed-length OTP need no truncation.
    /// </summary>
    internal static string Base32Encode(ReadOnlySpan<byte> data)
    {
        var output = new char[(data.Length * 8 + 4) / 5];
        var index = 0;
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;

            while (bitsLeft >= 5)
            {
                output[index++] = Base32Alphabet[(buffer >> (bitsLeft - 5)) & 0x1F];
                bitsLeft -= 5;
            }
        }

        if (bitsLeft > 0)
        {
            // Partial trailing group, left-aligned — never padding.
            output[index++] = Base32Alphabet[(buffer << (5 - bitsLeft)) & 0x1F];
        }

        return new string(output, 0, index);
    }

    /// <summary>
    /// SQ-5 / Q-A8: PBKDF2-SHA256(100 000 iterations) over a fresh 16-byte
    /// salt; returns <c>salt || hash</c> (48 bytes) for <c>otp_hash</c>.
    /// </summary>
    internal static byte[] HashOtp(string rawOtp)
    {
        var salt = RandomNumberGenerator.GetBytes(OtpSaltByteLength);
        var hash = KeyDerivation.Pbkdf2(
            rawOtp, salt, KeyDerivationPrf.HMACSHA256, OtpHashIterations, OtpHashByteLength);

        var stored = new byte[StoredHashByteLength];
        Buffer.BlockCopy(salt, 0, stored, 0, OtpSaltByteLength);
        Buffer.BlockCopy(hash, 0, stored, OtpSaltByteLength, OtpHashByteLength);
        return stored;
    }

    /// <summary>
    /// Recomputes PBKDF2 from the stored salt and compares in fixed time.
    /// Returns <c>false</c> for a malformed stored hash rather than throwing
    /// — a corrupt row must fail closed, like a wrong OTP.
    /// </summary>
    internal static bool VerifyHash(string rawOtp, byte[] storedHash)
    {
        if (storedHash is null || storedHash.Length != StoredHashByteLength)
        {
            return false;
        }

        var salt = new byte[OtpSaltByteLength];
        Buffer.BlockCopy(storedHash, 0, salt, 0, OtpSaltByteLength);

        var expected = KeyDerivation.Pbkdf2(
            rawOtp, salt, KeyDerivationPrf.HMACSHA256, OtpHashIterations, OtpHashByteLength);

        return CryptographicOperations.FixedTimeEquals(
            expected,
            storedHash.AsSpan(OtpSaltByteLength, OtpHashByteLength));
    }
}
