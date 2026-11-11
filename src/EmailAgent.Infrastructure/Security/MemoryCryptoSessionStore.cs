using System.Security.Cryptography;
using System.Text;
using EmailAgent.Core.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace EmailAgent.Infrastructure.Security;

/// <summary>
/// <see cref="IMemoryCache"/>-backed <see cref="ICryptoSessionStore"/> (M5T1,
/// SQ-M5-1): at most one live session per user, held entirely in process RAM.
/// </summary>
/// <remarks>
/// <para>
/// No-persistence-path property (structurally asserted by
/// <c>Store_HasNoDbContextDependency</c>): the constructor takes only
/// <see cref="IMemoryCache"/> and <see cref="IOptions{TOptions}"/> of
/// <see cref="CryptoSessionOptions"/> — there is no
/// <c>EmailAgentDbContext</c>, no table, no migration. A database dump can
/// therefore only ever contain KEK-wrapped <c>users.dek_wrapped</c>; the DEK
/// exists here only AES-GCM-wrapped under the session key, and the session
/// key only as raw material for the worker-path unwrap.
/// </para>
/// <para>
/// Per-process by design: a server restart destroys every session (users
/// re-unlock), and a multi-instance deployment would need sticky sessions or
/// a shared encrypted store — out of v1 scope, the Backend ships as a single
/// container (§4 / SQ-M5-10).
/// </para>
/// <para>
/// Concurrency: one coarse gate serializes entry mutation (create-replace,
/// touch, revoke, expiry eviction); entries are immutable records, so readers
/// always observe a whole entry. The session set per process is small
/// (one entry per unlocked user), so contention is negligible. Every cache
/// write also carries an absolute horizon (absolute + idle), so an entry that
/// expires without ever being read again still leaves RAM instead of
/// lingering until process restart.
/// </para>
/// <para>
/// No token, DEK, or session key is ever logged.
/// </para>
/// </remarks>
public sealed class MemoryCryptoSessionStore : ICryptoSessionStore
{
    private const int DekByteLength = 32;
    private const int SessionKeyByteLength = 32;
    private const int NonceByteLength = 12;
    private const int GcmTagByteLength = 16;

    private const string SessionKeyPrefix = "crypto-session/session/";
    private const string UserKeyPrefix = "crypto-session/user/";

    private readonly IMemoryCache _cache;
    private readonly CryptoSessionOptions _options;
    private readonly object _gate = new();

    /// <summary>
    /// Takes only the shared cache and the timeout options — deliberately no
    /// <c>EmailAgentDbContext</c>: the no-persistence-path property of the
    /// session store is structural (M5T1 SQ-M5-1).
    /// </summary>
    public MemoryCryptoSessionStore(IMemoryCache cache, IOptions<CryptoSessionOptions> options)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        if (!CryptoSessionOptions.IsValid(_options))
        {
            throw new OptionsValidationException(
                nameof(CryptoSessionOptions),
                typeof(CryptoSessionOptions),
                new[] { CryptoSessionOptions.InvalidMessage });
        }
    }

    /// <inheritdoc />
    public CryptoSessionHandle CreateOrReplace(Guid userId, ReadOnlyMemory<byte> dek, CancellationToken cancellationToken = default)
    {
        if (dek.Length != DekByteLength)
        {
            throw new ArgumentException(
                $"The data-encryption key must be exactly {DekByteLength} bytes.", nameof(dek));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;
        var absoluteExpiresAt = now + _options.AbsoluteTimeout;
        var idleExpiresAt = ClampIdle(now + _options.IdleTimeout, absoluteExpiresAt);

        // The session key stays in the entry for the life of the session (the
        // worker-path unwrap needs it without a presented token); it is
        // process-RAM material that dies with the entry — it is never logged
        // and never persisted.
        var sessionKey = RandomNumberGenerator.GetBytes(SessionKeyByteLength);
        var token = FormatToken(sessionKey);
        var nonce = RandomNumberGenerator.GetBytes(NonceByteLength);
        var wrapped = WrapDek(dek.Span, sessionKey, nonce);
        var entry = new CryptoSession(
            SessionId: Guid.NewGuid(),
            UserId: userId,
            TokenHash: HashToken(token),
            DekWrappedBySk: wrapped,
            SessionKey: sessionKey,
            CreatedAt: now,
            LastSeenAt: now,
            IdleExpiresAt: idleExpiresAt,
            AbsoluteExpiresAt: absoluteExpiresAt);

        lock (_gate)
        {
            // Idempotent replace (SQ-M5-1): at most one live session per
            // user — evict the previous entry from both key views first.
            RemoveUserEntry(userId);
            _cache.Set(SessionCacheKey(entry.SessionId), entry, EntryCacheOptions(entry));
            _cache.Set(UserCacheKey(userId), entry, EntryCacheOptions(entry));
        }

        // The raw token is returned once and never stored (only its hash).
        return new CryptoSessionHandle(entry.SessionId, token);
    }

    /// <inheritdoc />
    public CryptoSessionResult TryGetDek(Guid userId, string sessionToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            // A credential was required but none was presented.
            return CryptoSessionResult.Absent();
        }

        var entry = FindByUser(userId);
        if (entry is null)
        {
            // The presented token resolves to no live session (revoked,
            // replaced, restarted-away, or simply unknown).
            return CryptoSessionResult.Absent();
        }

        if (!TokenMatches(entry, sessionToken))
        {
            return CryptoSessionResult.Absent();
        }

        return ResolveDek(entry);
    }

    /// <inheritdoc />
    public CryptoSessionResult TryGetDekForUser(Guid userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entry = FindByUser(userId);
        if (entry is null)
        {
            // No session at all: the vault is locked (SQ-M5-10's transient
            // outcome for a job attempted while locked).
            return CryptoSessionResult.Locked();
        }

        return ResolveDek(entry);
    }

    /// <inheritdoc />
    public bool Touch(Guid userId, string sessionToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return false;
        }

        lock (_gate)
        {
            var entry = FindByUser(userId);
            if (entry is null || !TokenMatches(entry, sessionToken))
            {
                return false;
            }

            return RefreshIdle(entry);
        }
    }

    /// <inheritdoc />
    public bool TouchForUser(Guid userId)
    {
        lock (_gate)
        {
            var entry = FindByUser(userId);
            if (entry is null)
            {
                return false;
            }

            return RefreshIdle(entry);
        }
    }

    /// <inheritdoc />
    public Guid? RevokeByUser(Guid userId)
    {
        lock (_gate)
        {
            var entry = FindByUser(userId);
            if (entry is null)
            {
                return null;
            }

            RemoveEntry(entry);
            return entry.SessionId;
        }
    }

    /// <inheritdoc />
    public bool RevokeBySessionId(Guid sessionId)
    {
        lock (_gate)
        {
            if (!_cache.TryGetValue<CryptoSession>(SessionCacheKey(sessionId), out var entry)
                || entry is null)
            {
                return false;
            }

            RemoveEntry(entry);
            return true;
        }
    }

    /// <summary>
    /// Unwraps the entry's DEK — but only when the entry is still within both
    /// deadlines; a past idle or absolute deadline yields
    /// <see cref="CryptoSessionStatus.Expired"/> and evicts the entry (the
    /// user must re-unlock; nothing expired lingers in RAM).
    /// </summary>
    private CryptoSessionResult ResolveDek(CryptoSession entry)
    {
        var now = DateTimeOffset.UtcNow;
        if (now > entry.IdleExpiresAt || now > entry.AbsoluteExpiresAt)
        {
            lock (_gate)
            {
                RemoveEntry(entry);
            }

            return CryptoSessionResult.Expired();
        }

        try
        {
            return CryptoSessionResult.Ok(UnwrapDek(entry), entry.SessionId);
        }
        catch (CryptographicException)
        {
            // In-process entry failed GCM authentication — treat as no usable
            // session rather than surfacing a 500. Never log the material.
            return CryptoSessionResult.Locked();
        }
    }

    /// <summary>
    /// Sliding idle refresh: moves <c>last_seen_at</c> and the idle deadline
    /// to now + idle timeout, never past the absolute deadline. Returns
    /// <c>false</c> (leaving the entry untouched) when already expired.
    /// </summary>
    private bool RefreshIdle(CryptoSession entry)
    {
        var now = DateTimeOffset.UtcNow;
        if (now > entry.IdleExpiresAt || now > entry.AbsoluteExpiresAt)
        {
            RemoveEntry(entry);
            return false;
        }

        var touched = entry with
        {
            LastSeenAt = now,
            IdleExpiresAt = ClampIdle(now + _options.IdleTimeout, entry.AbsoluteExpiresAt),
        };

        _cache.Set(SessionCacheKey(touched.SessionId), touched, EntryCacheOptions(touched));
        _cache.Set(UserCacheKey(touched.UserId), touched, EntryCacheOptions(touched));
        return true;
    }

    /// <summary>Fixed-time comparison of <c>sha256(presented token)</c> against the stored hash.</summary>
    private static bool TokenMatches(CryptoSession entry, string presented)
    {
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        byte[] storedHash;
        try
        {
            storedHash = Convert.FromBase64String(entry.TokenHash);
        }
        catch (FormatException)
        {
            return false;
        }

        return presentedHash.Length == storedHash.Length
               && CryptographicOperations.FixedTimeEquals(presentedHash, storedHash);
    }

    private CryptoSession? FindByUser(Guid userId)
        => _cache.TryGetValue<CryptoSession>(UserCacheKey(userId), out var entry) ? entry : null;

    /// <summary>
    /// Cache horizon for an entry — deliberately <b>not</b> the logical
    /// deadline: those stay the store's own (<see cref="ResolveDek"/> and
    /// <see cref="RefreshIdle"/> enforce them and report
    /// <see cref="CryptoSessionStatus.Expired"/> to any late reader), while
    /// the cache only guarantees that a dead entry nobody ever touches again
    /// still leaves RAM. Absolute (never slides) at absolute + idle, so an
    /// active session's sliding refresh can never be undercut by eviction and
    /// retention stays bounded by the two configured timeouts.
    /// </summary>
    private MemoryCacheEntryOptions EntryCacheOptions(CryptoSession entry)
        => new() { AbsoluteExpiration = entry.AbsoluteExpiresAt + _options.IdleTimeout };

    private void RemoveEntry(CryptoSession entry)
    {
        _cache.Remove(SessionCacheKey(entry.SessionId));
        // The user-key view can already point at a REPLACEMENT entry when an
        // expired read races a re-unlock — only drop it when it still maps to
        // the entry being removed, so a stale eviction can never strand a
        // freshly minted live session.
        if (FindByUser(entry.UserId) is { } current
            && current.SessionId == entry.SessionId)
        {
            _cache.Remove(UserCacheKey(entry.UserId));
        }
    }

    private void RemoveUserEntry(Guid userId)
    {
        if (FindByUser(userId) is { } existing)
        {
            RemoveEntry(existing);
        }
    }

    /// <summary>The token the SPA holds once: base64url of the raw 256-bit session key.</summary>
    private static string FormatToken(byte[] sessionKey)
        => Convert.ToBase64String(sessionKey).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary><c>TokenHash = sha256(SK-token)</c> over the UTF-8 token text, base64-encoded.</summary>
    private static string HashToken(string token)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string SessionCacheKey(Guid sessionId) => SessionKeyPrefix + sessionId.ToString("N");

    private static string UserCacheKey(Guid userId) => UserKeyPrefix + userId.ToString("N");

    /// <summary>The idle deadline can never pass the absolute cap.</summary>
    private static DateTimeOffset ClampIdle(DateTimeOffset idle, DateTimeOffset absolute)
        => idle > absolute ? absolute : idle;

    /// <summary>
    /// <c>nonce(12) || ciphertext || tag(16)</c> under the session key — the
    /// same AES-256-GCM shape <see cref="Argon2idAesGcmEncryptionService"/>
    /// uses, but done with the platform <see cref="AesGcm"/> here: the store's
    /// constructor is pinned to cache + options only (SQ-M5-1's
    /// no-persistence-path contract), so it takes no
    /// <see cref="IEncryptionService"/>, and <c>WrapWithUserDek</c> would not
    /// fit the entry anyway — it binds an extra userId AAD and returns nonce
    /// and ciphertext separately instead of one self-contained
    /// <c>dek_wrapped_by_sk</c> blob.
    /// </summary>
    private static byte[] WrapDek(ReadOnlySpan<byte> dek, byte[] sessionKey, byte[] nonce)
    {
        var combined = new byte[NonceByteLength + dek.Length + GcmTagByteLength];
        nonce.CopyTo(combined);
        using var aes = new AesGcm(sessionKey, GcmTagByteLength);
        aes.Encrypt(
            nonce,
            dek,
            combined.AsSpan(NonceByteLength, dek.Length),
            combined.AsSpan(NonceByteLength + dek.Length, GcmTagByteLength));
        return combined;
    }

    /// <summary>Inverse of <see cref="WrapDek"/>; throws <see cref="CryptographicException"/> on tag mismatch.</summary>
    private static byte[] UnwrapDek(CryptoSession entry)
    {
        var blob = entry.DekWrappedBySk;
        if (blob.Length <= NonceByteLength + GcmTagByteLength)
        {
            throw new CryptographicException("The wrapped DEK is malformed.");
        }

        var nonce = blob.AsSpan(0, NonceByteLength);
        var ciphertext = blob.AsSpan(NonceByteLength, blob.Length - NonceByteLength - GcmTagByteLength);
        var tag = blob.AsSpan(blob.Length - GcmTagByteLength, GcmTagByteLength);
        var dek = new byte[ciphertext.Length];
        using var aes = new AesGcm(entry.SessionKey, GcmTagByteLength);
        aes.Decrypt(nonce, ciphertext, tag, dek);
        return dek;
    }
}
