using EmailAgent.Spa.Models;

namespace EmailAgent.Spa.Security;

/// <summary>
/// M5T1 (SQ-M5-1 / Q-O28): the browser-side cryptography seam of the unlock
/// flow — derives the KEK from the passphrase with Argon2id and AES-GCM
/// unwraps the DEK, both <b>in the browser</b>. The default implementation
/// (<see cref="JsUnlockCrypto"/>) runs the vendored argon2-browser WASM plus
/// <c>crypto.subtle</c> through the <c>emailAgent.*</c> JS interop functions;
/// tests substitute a deterministic fake (bUnit has no JS runtime), which is
/// why the boundary is an interface rather than static calls.
/// </summary>
/// <remarks>
/// No method persists anything: the passphrase, KEK and DEK live in typed
/// buffers for the duration of one unlock attempt and are zeroed by the
/// caller. A wrong passphrase surfaces as
/// <see cref="WrongPassphraseException"/> (the GCM tag check fails) — the
/// server never sees the passphrase, so it can never be the one to say no.
/// </remarks>
public interface IUnlockCrypto
{
    /// <summary>
    /// Argon2id over <paramref name="salt"/> with the account's stored cost
    /// parameters → the 32-byte KEK.
    /// </summary>
    Task<byte[]> DeriveKekAsync(
        string passphrase,
        byte[] salt,
        KdfParamsDto kdfParams,
        CancellationToken ct = default);

    /// <summary>
    /// AES-256-GCM unwrap of <paramref name="dekWrapped"/> under the KEK with
    /// <paramref name="dekNonce"/> → the 32-byte DEK.
    /// </summary>
    /// <exception cref="WrongPassphraseException">The GCM tag did not authenticate.</exception>
    /// <exception cref="UnlockCryptoException">Malformed wrap/nonce or an interop failure.</exception>
    Task<byte[]> UnwrapDekAsync(
        ReadOnlyMemory<byte> kek,
        byte[] dekWrapped,
        byte[] dekNonce,
        CancellationToken ct = default);
}

/// <summary>The passphrase-derived KEK did not authenticate the wrapped DEK (GCM tag failure).</summary>
public sealed class WrongPassphraseException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>The wrap/nonce/parameters were unusable or the browser crypto interop failed.</summary>
public sealed class UnlockCryptoException(string message, Exception? inner = null)
    : Exception(message, inner);
