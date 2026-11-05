using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using EmailAgent.Core.Security;
using Isopoh.Cryptography.Argon2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmailAgent.Infrastructure.Security;

/// <summary>
/// M1T3 encryption service: Argon2id KEK derivation (Isopoh, Q-E2) and
/// AES-256-GCM DEK wrap/unwrap (Q-E3/Q-E6), plus the system-key helpers for
/// system-owned secrets (Q-E7). Library-only — no HTTP, no EF Core.
/// </summary>
/// <remarks>
/// <para>
/// Package note: this uses the <c>Argon2Config</c>-based API of
/// <c>Isopoh.Cryptography.Argon2</c> 2.0.0 (<c>Argon2Type.HybridAddressing</c>
/// = Argon2id). The task file's <c>new Argon2id(...) { MemorySizeKb = ... }</c>
/// snippet targets a newer/different Isopoh surface that this package version
/// does not expose. Fallback if native execution ever fails on a platform:
/// <c>Konscious.Security.Cryptography.Argon2</c> (see M1T3 risk note; M10's
/// Windows verification decides).
/// </para>
/// <para>
/// KEK-derivation mutex (Q-E5): a per-user <see cref="SemaphoreSlim"/> held
/// with a zero wait for the duration of each derivation. Excess concurrent
/// callers for the same user observe a typed
/// <see cref="KekDerivationBusyException"/> (HTTP 429 mapping is M1T6's).
/// Per-process only — cross-process coordination is out of scope for M1.
/// </para>
/// <para>
/// No passphrase, DEK, KEK, system key, or salt value is ever logged (the
/// logging-scanning unit test enforces this).
/// </para>
/// </remarks>
public sealed class Argon2idAesGcmEncryptionService : IEncryptionService
{
    private const int DekByteLength = 32;
    private const int NonceByteLength = 12;
    private const int GcmTagByteLength = 16;

    // Defensive bounds for stored KdfParams (users.kdf_params is fed back in
    // by M1T6/M1T9): generous headroom over Q-E2 (64 MiB / t=3 / p=4) while
    // still rejecting corrupt or hostile values before Argon2id allocates.
    private const int MinSaltByteLength = 8; // Argon2 specification floor
    private const int MaxIterations = 64;
    private const int MaxParallelism = 64;
    private const int MaxMemoryKb = 1048576; // 1 GiB

    private readonly byte[] _systemKey;
    private readonly ILogger<Argon2idAesGcmEncryptionService> _logger;

    // One zero-wait SemaphoreSlim per user, for the lifetime of the process.
    // Entries are never evicted: M1 is per-process only and a session set of
    // users is small; revisit at M1T6/M5 if a long-lived process ever sees
    // unbounded user churn (documented Q-E5 scope).
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _userMutexes = new();

    /// <summary>
    /// Loads the system KEK once at startup from
    /// <c>EncryptionOptions.KeyEncryptionKey</c> (bound from the
    /// <c>System</c> section / <c>SYSTEM__KeyEncryptionKey</c>). Throws
    /// <see cref="OptionsValidationException"/> when the key is missing or
    /// shorter than 32 bytes (the <c>ValidateOnStart</c> validator rejects
    /// the same conditions before the host starts).
    /// </summary>
    public Argon2idAesGcmEncryptionService(
        IOptions<EncryptionOptions> options,
        ILogger<Argon2idAesGcmEncryptionService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var configured = options?.Value?.KeyEncryptionKey;
        if (!EncryptionOptions.IsValid(configured))
        {
            throw new OptionsValidationException(
                nameof(EncryptionOptions),
                typeof(EncryptionOptions),
                new[] { EncryptionOptions.InvalidKeyMessage });
        }

        _systemKey = EncryptionOptions.ResolveKeyBytes(configured!);
    }

    /// <inheritdoc />
    public KdfParams GetDefaultKdfParams() => KdfParams.Default();

    /// <inheritdoc />
    public byte[] DeriveKek(Guid userId, string passphrase, byte[] salt, KdfParams parameters)
    {
        EnterKekDerivation(userId);
        try
        {
            _logger.LogDebug(
                "Deriving KEK for user {UserId} (m={MemoryKb}KiB t={Iterations} p={Parallelism}).",
                userId,
                parameters.MemoryKb,
                parameters.Iterations,
                parameters.Parallelism);
            return DeriveKekCore(passphrase, salt, parameters);
        }
        finally
        {
            ExitKekDerivation(userId);
        }
    }

    /// <inheritdoc />
    public WrappedDek GenerateAndWrapDek(
        Guid userId,
        string passphrase,
        byte[] salt,
        KdfParams parameters)
    {
        EnterKekDerivation(userId);
        try
        {
            var dek = RandomNumberGenerator.GetBytes(DekByteLength);
            var nonce = RandomNumberGenerator.GetBytes(NonceByteLength);
            var kek = DeriveKekCore(passphrase, salt, parameters);
            return new WrappedDek(AesGcmWrap(dek, kek, nonce), nonce, dek);
        }
        finally
        {
            ExitKekDerivation(userId);
        }
    }

    /// <inheritdoc />
    public byte[] UnwrapDek(
        Guid userId,
        string passphrase,
        byte[] salt,
        KdfParams parameters,
        byte[] dekWrapped,
        byte[] dekNonce)
    {
        // Shape check before paying for Argon2id: the contract promises the
        // typed exception for every non-authenticating input (wrong passphrase,
        // nonce, or ciphertext), including a malformed stored wrap — otherwise
        // AesGcm would surface an untyped ArgumentException for a wrong-length
        // nonce. No secret material in the message.
        if (dekNonce.Length != NonceByteLength || dekWrapped.Length <= GcmTagByteLength)
        {
            throw new InvalidPassphraseException(
                $"Malformed DEK wrap: nonce must be {NonceByteLength} bytes and the wrapped blob must exceed the {GcmTagByteLength}-byte GCM tag.");
        }

        EnterKekDerivation(userId);
        try
        {
            var kek = DeriveKekCore(passphrase, salt, parameters);
            try
            {
                return AesGcmUnwrap(dekWrapped, kek, dekNonce);
            }
            catch (CryptographicException ex)
            {
                // GCM tag mismatch: wrong passphrase (or tampered inputs).
                // The typed exception carries no secret material.
                throw new InvalidPassphraseException(
                    "The supplied passphrase does not unwrap this account's data-encryption key.",
                    ex);
            }
        }
        finally
        {
            ExitKekDerivation(userId);
        }
    }

    /// <inheritdoc />
    public WrappedSecret WrapWithSystemKey(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceByteLength);
        return new WrappedSecret(AesGcmWrap(plaintext, _systemKey, nonce), nonce);
    }

    /// <inheritdoc />
    public byte[] UnwrapWithSystemKey(byte[] ciphertext, byte[] nonce)
    {
        // Wrong key / nonce surfaces as CryptographicException: it indicates
        // deployment misconfiguration (SYSTEM__KeyEncryptionKey mismatch),
        // not a caller error, so no passphrase-style typed exception wraps it.
        return AesGcmUnwrap(ciphertext, _systemKey, nonce);
    }

    /// <summary>
    /// Argon2id KEK derivation without mutex handling — callers hold the
    /// per-user mutex already (the mutex is reentrant-unsafe, so the public
    /// entry points never nest it).
    /// </summary>
    private static byte[] DeriveKekCore(string passphrase, byte[] salt, KdfParams parameters)
    {
        ValidateDerivationInputs(salt, parameters);

        var config = new Argon2Config
        {
            // HybridAddressing = Argon2id (Q-E2); Nineteen = Argon2 v1.3.
            Type = Argon2Type.HybridAddressing,
            Version = Argon2Version.Nineteen,
            Password = Encoding.UTF8.GetBytes(passphrase),
            Salt = salt, // 16 random bytes per user (users.kdf_salt)
            MemoryCost = parameters.MemoryKb, // KiB (65536 = 64 MiB)
            TimeCost = parameters.Iterations, // t = 3
            Lanes = parameters.Parallelism, // p = 4
            Threads = parameters.Parallelism,
            HashLength = parameters.HashLength, // 32-byte (256-bit) KEK
        };

        var argon2 = new Argon2(config);
        using var hash = argon2.Hash();

        // Copy out of the SecureArray before disposal zeroes it.
        var buffer = hash.Buffer;
        var kek = new byte[buffer.Length];
        for (var i = 0; i < buffer.Length; i++)
        {
            kek[i] = buffer[i];
        }

        return kek;
    }

    /// <summary>
    /// Cheap bounds check on the per-user KDF inputs before Argon2id runs:
    /// a corrupt or hostile <c>kdf_params</c> row must not trigger a
    /// multi-gigabyte allocation, a non-256-bit KEK (which AES-GCM would
    /// reject later), or an under-sized salt. Defaults (Q-E2) always pass.
    /// </summary>
    private static void ValidateDerivationInputs(byte[] salt, KdfParams parameters)
    {
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(parameters);

        if (salt.Length < MinSaltByteLength)
        {
            throw new ArgumentException(
                $"Salt must be at least {MinSaltByteLength} bytes (Argon2).", nameof(salt));
        }

        if (parameters.Iterations < 1 || parameters.Iterations > MaxIterations)
        {
            throw new ArgumentException(
                $"KDF iterations must be between 1 and {MaxIterations}.", nameof(parameters));
        }

        if (parameters.Parallelism < 1 || parameters.Parallelism > MaxParallelism)
        {
            throw new ArgumentException(
                $"KDF parallelism must be between 1 and {MaxParallelism}.", nameof(parameters));
        }

        if (parameters.MemoryKb < 8 * parameters.Parallelism
            || parameters.MemoryKb > MaxMemoryKb)
        {
            throw new ArgumentException(
                $"KDF memory must be between 8*parallelism and {MaxMemoryKb} KiB.",
                nameof(parameters));
        }

        if (parameters.HashLength != DekByteLength)
        {
            throw new ArgumentException(
                $"KDF hash length must be exactly {DekByteLength} bytes (256-bit AES-GCM key).",
                nameof(parameters));
        }
    }

    /// <summary>
    /// AES-256-GCM wrap: returns ciphertext || tag (plaintext length + 16
    /// bytes), with a fresh 12-byte nonce supplied by the caller.
    /// </summary>
    private static byte[] AesGcmWrap(byte[] plaintext, byte[] key, byte[] nonce)
    {
        var combined = new byte[plaintext.Length + GcmTagByteLength];
        using var aes = new AesGcm(key, GcmTagByteLength);
        aes.Encrypt(
            nonce,
            plaintext,
            combined.AsSpan(0, plaintext.Length),
            combined.AsSpan(plaintext.Length, GcmTagByteLength));
        return combined;
    }

    /// <summary>
    /// AES-256-GCM unwrap over ciphertext || tag; throws
    /// <see cref="CryptographicException"/> on tag mismatch or malformed input.
    /// </summary>
    private static byte[] AesGcmUnwrap(byte[] combined, byte[] key, byte[] nonce)
    {
        if (combined.Length <= GcmTagByteLength)
        {
            throw new CryptographicException(
                "Ciphertext is shorter than the GCM tag; it cannot be authentic.");
        }

        var plaintextLength = combined.Length - GcmTagByteLength;
        var plaintext = new byte[plaintextLength];
        using var aes = new AesGcm(key, GcmTagByteLength);
        aes.Decrypt(
            nonce,
            combined.AsSpan(0, plaintextLength),
            combined.AsSpan(plaintextLength, GcmTagByteLength),
            plaintext);
        return plaintext;
    }

    private SemaphoreSlim GetMutex(Guid userId)
        => _userMutexes.GetOrAdd(userId, static _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Zero-wait acquisition of the per-user KEK mutex; the loser observes a
    /// typed <see cref="KekDerivationBusyException"/> (Q-E5 → M1T6's 429).
    /// </summary>
    private void EnterKekDerivation(Guid userId)
    {
        if (!GetMutex(userId).Wait(0))
        {
            _logger.LogWarning(
                "KEK derivation already in progress for user {UserId}; rejecting the concurrent caller (Q-E5).",
                userId);
            throw new KekDerivationBusyException();
        }
    }

    private void ExitKekDerivation(Guid userId) => GetMutex(userId).Release();
}