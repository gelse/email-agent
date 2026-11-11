using System.Net;
using System.Security.Cryptography;
using EmailAgent.Spa.Auth;
using EmailAgent.Spa.Models;

namespace EmailAgent.Spa.Security;

/// <summary>Every terminal outcome of one unlock attempt, mapped by <see cref="UnlockService"/>.</summary>
public enum UnlockStatus
{
    /// <summary>The server minted a session; the token is in <see cref="TokenStore.CryptoSession"/>.</summary>
    Unlocked,

    /// <summary>The passphrase did not unwrap the DEK (in-browser GCM tag failure — never sent to the server).</summary>
    WrongPassphrase,

    /// <summary>409 <c>/errors/passphrase-not-set</c> — the first-login passphrase flow is still owed.</summary>
    PassphraseNotSet,

    /// <summary>The session itself failed (401/403) — fix by signing in, never by retyping the passphrase.</summary>
    NotAuthorized,

    /// <summary>429 — an unlock/derivation budget is already spent.</summary>
    RateLimited,

    /// <summary>The API could not be reached (transport failure or cancellation).</summary>
    Unreachable,

    /// <summary>Anything else (400 <c>/errors/dek-invalid</c>, unusable stored material, 5xx, interop failure).</summary>
    Failed,
}

/// <summary>The result of one unlock attempt; <see cref="Message"/> is already user-facing (never key material).</summary>
public sealed record UnlockOutcome(UnlockStatus Status, string? Message = null)
{
    public bool Unlocked => Status == UnlockStatus.Unlocked;
}

/// <summary>
/// M5T1 (SQ-M5-1 / §13.17): the SPA side of the vault-unlock flow —
/// <c>GET /v1/account/kdf</c> → Argon2id KEK + AES-GCM unwrap through
/// <see cref="IUnlockCrypto"/> (in the browser) → <c>POST /v1/account/unlock</c>
/// → the opaque session token cached in <see cref="TokenStore.CryptoSession"/>.
/// </summary>
/// <remarks>
/// The passphrase is handed only to <see cref="IUnlockCrypto"/> and is never
/// persisted, logged or sent anywhere; the DEK exists in a buffer between the
/// unwrap and the unlock POST and is zeroed afterwards. A wrong passphrase is
/// decided in the browser (the GCM tag check), so the server cannot rate-limit
/// guesses — the Argon2id cost itself is the brake (the same Q-E2 parameters
/// the server used before this flow).
/// </remarks>
public sealed class UnlockService(
    AccountClient account,
    IUnlockCrypto crypto,
    TokenStore tokenStore)
{
    // Mirrors Argon2idAesGcmEncryptionService.ValidateDerivationInputs (M1T3):
    // reject hostile/corrupt stored parameters BEFORE Argon2id allocates.
    private const int MinSaltByteLength = 8;
    private const int MinIterations = 1;
    private const int MaxIterations = 64;
    private const int MinParallelism = 1;
    private const int MaxParallelism = 64;
    private const int MinMemoryKbPerLane = 8;
    private const int MaxMemoryKb = 1048576;
    private const int DekByteLength = 32;
    private const int NonceByteLength = 12;
    private const int GcmTagByteLength = 16;

    /// <summary>Runs one full unlock attempt with the typed passphrase.</summary>
    public async Task<UnlockOutcome> UnlockAsync(string passphrase, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(passphrase))
        {
            return new UnlockOutcome(UnlockStatus.Failed, "The passphrase is required.");
        }

        // 1. The non-secret material (409 → first-login link; 401/403 → sign in).
        KdfMaterial material;
        try
        {
            material = await account.GetKdfMaterialAsync(ct).ConfigureAwait(false);
        }
        catch (AccountApiException ex)
        {
            return MapAccountFailure(ex);
        }
        catch (HttpRequestException)
        {
            return new UnlockOutcome(UnlockStatus.Unreachable, "Could not reach the API.");
        }
        catch (OperationCanceledException)
        {
            return new UnlockOutcome(UnlockStatus.Unreachable, "Could not reach the API.");
        }

        var invalid = Validate(material);
        if (invalid is not null)
        {
            return new UnlockOutcome(UnlockStatus.Failed, invalid);
        }

        // 2. Browser-side derivation + unwrap. The KEK and DEK are zeroed in
        //    every path that produced them (the DEK below outlives this block
        //    only long enough to be POSTed once).
        var dek = Array.Empty<byte>();
        try
        {
            var kek = Array.Empty<byte>();
            try
            {
                kek = await crypto
                    .DeriveKekAsync(passphrase, material.KdfSalt, material.KdfParams, ct)
                    .ConfigureAwait(false);
                dek = await crypto
                    .UnwrapDekAsync(kek, material.DekWrapped, material.DekNonce, ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (kek.Length > 0)
                {
                    CryptographicOperations.ZeroMemory(kek);
                }
            }
        }
        catch (WrongPassphraseException)
        {
            return new UnlockOutcome(UnlockStatus.WrongPassphrase,
                "The passphrase is not valid.");
        }
        catch (UnlockCryptoException)
        {
            // Malformed wrap / interop failure — the shape problems the user
            // can act on were already caught by Validate() above.
            return new UnlockOutcome(UnlockStatus.Failed,
                "Something went wrong — please try again.");
        }
        catch (OperationCanceledException)
        {
            return new UnlockOutcome(UnlockStatus.Unreachable, "Could not reach the API.");
        }

        // 3. Hand the DEK to the server once; it comes back as an opaque token.
        try
        {
            var response = await account
                .UnlockAsync(Convert.ToBase64String(dek), ct)
                .ConfigureAwait(false);
            tokenStore.CryptoSession.SetToken(response.CryptoSessionToken);
            return new UnlockOutcome(UnlockStatus.Unlocked);
        }
        catch (AccountApiException ex)
        {
            return MapAccountFailure(ex);
        }
        catch (HttpRequestException)
        {
            return new UnlockOutcome(UnlockStatus.Unreachable, "Could not reach the API.");
        }
        catch (OperationCanceledException)
        {
            return new UnlockOutcome(UnlockStatus.Unreachable, "Could not reach the API.");
        }
        finally
        {
            if (dek.Length > 0)
            {
                CryptographicOperations.ZeroMemory(dek);
            }
        }
    }

    /// <summary>
    /// One mapping for both flow legs: 409 → first-login, 401/403 → the
    /// session (not the passphrase) failed, 429 → budget, everything else →
    /// a generic/server-provided message.
    /// </summary>
    private static UnlockOutcome MapAccountFailure(AccountApiException ex)
    {
        if (ex.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // The passphrase was never judged — blaming it would send the
            // user in circles retyping a correct secret; the caller signs in.
            return new UnlockOutcome(UnlockStatus.NotAuthorized, ex.Detail);
        }

        if (ex.ProblemType == "/errors/passphrase-not-set"
            || ex.Status == HttpStatusCode.Conflict)
        {
            return new UnlockOutcome(UnlockStatus.PassphraseNotSet,
                ex.Detail ?? "No passphrase has been set for this account yet.");
        }

        if (ex.Status == HttpStatusCode.TooManyRequests)
        {
            return new UnlockOutcome(UnlockStatus.RateLimited,
                ex.Detail ?? "Too many unlock attempts — try again shortly.");
        }

        return new UnlockOutcome(UnlockStatus.Failed,
            (int)ex.Status >= 500
                ? "Something went wrong — please try again."
                : ex.Detail ?? "Something went wrong — please try again.");
    }

    /// <summary>Bounds-checks the stored material before any derivation; null when usable.</summary>
    private static string? Validate(KdfMaterial material)
    {
        var p = material.KdfParams;
        if (material.KdfSalt.Length < MinSaltByteLength)
        {
            return MalformedMaterial();
        }

        if (p.Iterations is < MinIterations or > MaxIterations
            || p.Parallelism is < MinParallelism or > MaxParallelism
            || p.MemoryKb < MinMemoryKbPerLane * p.Parallelism
            || p.MemoryKb > MaxMemoryKb
            || p.HashLength != DekByteLength)
        {
            return MalformedMaterial();
        }

        if (material.DekNonce.Length != NonceByteLength
            || material.DekWrapped.Length <= GcmTagByteLength)
        {
            return MalformedMaterial();
        }

        return null;

        static string MalformedMaterial()
            => "This account's stored key material is malformed — set the passphrase again.";
    }
}
