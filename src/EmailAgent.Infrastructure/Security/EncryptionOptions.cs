using System.Text;

namespace EmailAgent.Infrastructure.Security;

/// <summary>
/// Options for the per-install system KEK. Binds the <c>System</c>
/// configuration section, i.e. the documented
/// <c>SYSTEM__KeyEncryptionKey</c> environment variable (§16 / SQ-3 / Q-E7;
/// docker/entrypoint.sh materializes it from the <c>system_kek</c> secret).
/// </summary>
/// <remarks>
/// <para>
/// Key-length rule: the configured value must be at least 32 UTF-8 bytes —
/// the AES-256 key size floor. A value of exactly 32 bytes is used verbatim;
/// a <b>longer</b> value is not truncated lossily but hashed down to exactly
/// 32 bytes with SHA-256, so any passphrase-grade string can serve as the
/// system key deterministically on every host of the install.
/// </para>
/// <para>
/// The validation is enforced twice: by the <c>ValidateOnStart</c> predicate
/// registered in <c>AddEncryption</c> (the host refuses to start with a
/// missing/short key) and defensively by the
/// <see cref="Argon2idAesGcmEncryptionService"/> constructor for hosts that
/// construct the service directly.
/// </para>
/// </remarks>
public sealed class EncryptionOptions
{
    /// <summary>Configuration section name (<c>SYSTEM__*</c> env vars land here).</summary>
    public const string SectionName = "System";

    /// <summary>AES-256 key size in bytes (the floor and the effective length).</summary>
    public const int KeyByteLength = 32;

    /// <summary>Failure message shared by the options validator and the constructor guard.</summary>
    public const string InvalidKeyMessage =
        "System:KeyEncryptionKey (env SYSTEM__KeyEncryptionKey) must be present and at least "
        + "32 UTF-8 bytes; it is the per-install system KEK (§16 / Q-E7).";

    /// <summary>The raw configured key; never logged. See remarks for the length rule.</summary>
    public string? KeyEncryptionKey { get; set; }

    /// <summary>
    /// Whether <paramref name="key"/> satisfies the ≥ 32 UTF-8-byte rule —
    /// used by the startup validator and the service constructor guard.
    /// </summary>
    public static bool IsValid(string? key)
        => !string.IsNullOrWhiteSpace(key)
           && Encoding.UTF8.GetByteCount(key) >= KeyByteLength;

    /// <summary>
    /// Resolves the configured string to the effective 32-byte AES key:
    /// verbatim at exactly 32 bytes, SHA-256-hashed when longer (truncation
    /// rule above). Callers must pass a key that <see cref="IsValid"/> accepted.
    /// </summary>
    internal static byte[] ResolveKeyBytes(string key)
    {
        var raw = Encoding.UTF8.GetBytes(key);
        return raw.Length == KeyByteLength
            ? raw
            : System.Security.Cryptography.SHA256.HashData(raw);
    }
}