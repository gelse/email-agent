using EmailAgent.Spa.Models;
using Microsoft.JSInterop;

namespace EmailAgent.Spa.Security;

/// <summary>
/// M5T1 (Q-O28): the default <see cref="IUnlockCrypto"/> — the two
/// browser-side steps behind the <c>emailAgent.deriveKek</c> /
/// <c>emailAgent.unwrapDek</c> JS interop functions defined in
/// <c>wwwroot/js/unlock-crypto.js</c>. That script uses the vendored
/// argon2-browser WASM (Argon2id v1.3, matching the server's Isopoh
/// configuration) for the KEK and <c>crypto.subtle</c> for the AES-256-GCM
/// unwrap; the two are separate calls so the KEK never crosses an interop
/// boundary it does not have to (the DEK does — once, in base64, straight
/// into the unlock POST the service makes).
/// </summary>
/// <remarks>
/// JS errors are rejected promises; the script tags them with a stable
/// <c>emailAgent:</c> prefix so a wrong passphrase (GCM tag failure) is
/// told apart from a harness/interop failure without parsing free text.
/// </remarks>
public sealed class JsUnlockCrypto(IJSRuntime js) : IUnlockCrypto
{
    /// <summary>Marker the JS puts on a GCM tag-mismatch rejection.</summary>
    public const string PassphraseMismatchMarker = "emailAgent:passphrase-mismatch";

    /// <summary>Marker the JS puts on a malformed wrap/nonce rejection.</summary>
    public const string MalformedWrapMarker = "emailAgent:malformed-wrap";

    /// <summary>Marker the JS puts on out-of-range Argon2id parameter rejection.</summary>
    public const string MalformedParamsMarker = "emailAgent:malformed-params";

    /// <inheritdoc />
    public async Task<byte[]> DeriveKekAsync(
        string passphrase, byte[] salt, KdfParamsDto kdfParams, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(passphrase);
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(kdfParams);

        string kekB64;
        try
        {
            kekB64 = await js.InvokeAsync<string>(
                "emailAgent.deriveKek",
                ct,
                passphrase,
                Convert.ToBase64String(salt),
                kdfParams.MemoryKb,
                kdfParams.Iterations,
                kdfParams.Parallelism,
                kdfParams.HashLength).ConfigureAwait(false);
        }
        catch (JSException ex) when (MalformedParams(ex))
        {
            throw new UnlockCryptoException("The stored KDF parameters are unusable.", ex);
        }
        catch (JSException ex)
        {
            // The derive leg can reject too (argon2-not-loaded, no-webcrypto,
            // a raw WASM failure) — without this map the JSException would
            // escape UnlockService's typed catches unclassified.
            throw new UnlockCryptoException("The browser key derivation failed.", ex);
        }

        try
        {
            return Convert.FromBase64String(kekB64);
        }
        catch (FormatException ex)
        {
            throw new UnlockCryptoException("The browser returned an unusable derived key.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<byte[]> UnwrapDekAsync(
        ReadOnlyMemory<byte> kek, byte[] dekWrapped, byte[] dekNonce, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dekWrapped);
        ArgumentNullException.ThrowIfNull(dekNonce);

        string dekB64;
        try
        {
            dekB64 = await js.InvokeAsync<string>(
                "emailAgent.unwrapDek",
                ct,
                Convert.ToBase64String(kek.Span),
                Convert.ToBase64String(dekWrapped),
                Convert.ToBase64String(dekNonce)).ConfigureAwait(false);
        }
        catch (JSException ex) when (PassphraseMismatch(ex))
        {
            throw new WrongPassphraseException(
                "The supplied passphrase does not unwrap this account's data-encryption key.", ex);
        }
        catch (JSException ex) when (MalformedWrap(ex))
        {
            throw new UnlockCryptoException("The account's wrapped key is malformed.", ex);
        }
        catch (JSException ex)
        {
            throw new UnlockCryptoException("The browser crypto operation failed.", ex);
        }

        try
        {
            return Convert.FromBase64String(dekB64);
        }
        catch (FormatException ex)
        {
            throw new UnlockCryptoException("The browser returned an unusable unwrapped key.", ex);
        }
    }

    private static bool PassphraseMismatch(JSException ex)
        => ex.Message.Contains(PassphraseMismatchMarker, StringComparison.Ordinal);

    private static bool MalformedWrap(JSException ex)
        => ex.Message.Contains(MalformedWrapMarker, StringComparison.Ordinal);

    private static bool MalformedParams(JSException ex)
        => ex.Message.Contains(MalformedParamsMarker, StringComparison.Ordinal);
}
