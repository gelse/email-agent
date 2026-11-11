using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmailAgent.Api.Endpoints;

/// <summary>
/// M5T1 (SQ-M5-1 / §13.17): the wire bodies of the account-namespace unlock
/// flow — <c>POST /v1/account/unlock</c> and its prerequisite read
/// <c>GET /v1/account/kdf</c>. Neither route sits in the eight §13.3 M5
/// <c>/v1/user/*</c> count, so M5T6/M5T7's route totals are unchanged.
/// </summary>
public static class UnlockDtos
{
    /// <summary>The 32-byte AES-256 key length a submitted DEK must decode to.</summary>
    public const int DekByteLength = 32;
}

/// <summary>
/// Raised when the submitted <c>dek</c> is not base64 or does not decode to
/// exactly 32 bytes (M5T1: mapped by the unlock endpoint to RFC 7807 400
/// <c>/errors/dek-invalid</c> — never a 500 from an AES-GCM length check).
/// The message names the shape problem only and never echoes the value.
/// </summary>
public sealed class InvalidDekException : Exception
{
    public InvalidDekException(string reason)
        : base(reason)
    {
    }

    /// <summary>The stable <c>errors[0].reason</c> text (also the message).</summary>
    public string Reason => Message;
}

/// <summary>
/// Static, unit-testable decoding of the unlock body's <c>dek</c>: base64 →
/// exactly <see cref="UnlockDtos.DekByteLength"/> bytes, else
/// <see cref="InvalidDekException"/>. Kept off the handler so the validation
/// rule is testable without an <c>HttpContext</c> (task step 5 / AC).
/// </summary>
public static class UnlockDekCodec
{
    /// <summary>
    /// Decodes <paramref name="dek"/> to the raw 32-byte key.
    /// </summary>
    /// <exception cref="InvalidDekException">Not base64, or not exactly 32 bytes.</exception>
    public static byte[] Decode(string? dek)
    {
        if (string.IsNullOrWhiteSpace(dek))
        {
            throw new InvalidDekException(
                $"the dek field is required and must decode to {UnlockDtos.DekByteLength} bytes.");
        }

        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(dek.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidDekException("the dek field must be base64.");
        }

        if (raw.Length != UnlockDtos.DekByteLength)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw);
            throw new InvalidDekException(
                $"the dek field must decode to {UnlockDtos.DekByteLength} bytes.");
        }

        return raw;
    }
}

/// <summary>Body of <c>POST /v1/account/unlock</c> — the passphrase never leaves the browser.</summary>
public sealed record UnlockRequest
{
    /// <summary>Base64 of the raw 32-byte DEK the browser unwrapped with the passphrase-derived KEK.</summary>
    [JsonPropertyName("dek")]
    public string? Dek { get; init; }
}

/// <summary>200 body of <c>POST /v1/account/unlock</c> — the token is shown exactly once.</summary>
public sealed record UnlockResponse
{
    /// <summary>The new (or replaced) crypto-session id — safe to log/display.</summary>
    [JsonPropertyName("crypto_session_id")]
    public Guid CryptoSessionId { get; init; }

    /// <summary>The opaque session-key token: held in memory like the JWT, never persisted.</summary>
    [JsonPropertyName("crypto_session_token")]
    public string CryptoSessionToken { get; init; } = string.Empty;
}

/// <summary>
/// 200 body of <c>GET /v1/account/kdf</c> — everything the browser needs to
/// derive the KEK and unwrap the DEK: a salt, public KDF parameters and the
/// KEK-wrapped DEK ciphertext (all non-secret; the server cannot decrypt it
/// without the passphrase).
/// </summary>
public sealed record KdfMaterialResponse
{
    /// <summary>Base64 of the per-account Argon2id salt (<c>users.kdf_salt</c>).</summary>
    [JsonPropertyName("kdf_salt")]
    public string KdfSalt { get; init; } = string.Empty;

    /// <summary>The Argon2id parameters (<c>users.kdf_params</c>) as stored.</summary>
    [JsonPropertyName("kdf_params")]
    public JsonElement KdfParams { get; init; }

    /// <summary>Base64 of the KEK-wrapped DEK (<c>users.dek_wrapped</c>) — ciphertext only.</summary>
    [JsonPropertyName("dek_wrapped")]
    public string DekWrapped { get; init; } = string.Empty;

    /// <summary>Base64 of the 12-byte GCM nonce the wrap used (<c>users.dek_nonce</c>).</summary>
    [JsonPropertyName("dek_nonce")]
    public string DekNonce { get; init; } = string.Empty;
}

/// <summary>Source-gen context for the unlock-flow bodies (AOT-friendly, task step 5 rule).</summary>
[JsonSerializable(typeof(UnlockRequest))]
[JsonSerializable(typeof(UnlockResponse))]
[JsonSerializable(typeof(KdfMaterialResponse))]
public partial class UnlockJsonContext : JsonSerializerContext
{
}
