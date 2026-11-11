using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmailAgent.Spa.Auth;

namespace EmailAgent.Spa.Models;

/// <summary>
/// M5T1: the account-namespace client of the §13.17 unlock flow —
/// <c>GET /v1/account/kdf</c> (the non-secret material the browser needs to
/// derive the KEK and unwrap the DEK) and <c>POST /v1/account/unlock</c>
/// (the DEK, once, in exchange for the opaque session token). The passphrase
/// never leaves the device (SQ-M5-1); only the wrapped DEK crosses the wire
/// on unlock. Snake-case members pin the API's <c>UnlockDtos</c> wire names
/// (§13.13) — a wire change breaks compilation/tests on both sides.
/// </summary>
public sealed class AccountClient(HttpClient http)
{
    /// <summary>Paths this client speaks (kept here so tests can assert on them).</summary>
    public const string KdfPath = "/v1/account/kdf";

    public const string UnlockPath = "/v1/account/unlock";

    /// <summary>
    /// <c>GET /v1/account/kdf</c> — decodes the base64 material to bytes.
    /// </summary>
    /// <exception cref="AccountApiException">A non-200 answer (409 <c>/errors/passphrase-not-set</c>, 401, 403, …).</exception>
    /// <exception cref="HttpRequestException">Transport failure.</exception>
    public async Task<KdfMaterial> GetKdfMaterialAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync(KdfPath, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await AccountApiException.FromAsync(response, ct).ConfigureAwait(false);
        }

        var body = await response.Content
            .ReadFromJsonAsync<KdfMaterialResponse>(cancellationToken: ct)
            .ConfigureAwait(false);
        if (body is null)
        {
            throw new AccountApiException(HttpStatusCode.OK, null,
                "The KDF material response was empty.");
        }

        try
        {
            return body.ToMaterial();
        }
        catch (FormatException ex)
        {
            // The server shipped material the browser cannot use (a corrupt
            // row, not a user error): fail closed with a typed problem.
            throw new AccountApiException(HttpStatusCode.OK, null,
                "The account's key material is malformed.", ex);
        }
    }

    /// <summary>
    /// <c>POST /v1/account/unlock</c> — sends the unwrapped DEK once and
    /// returns the fresh crypto-session handle (id + token shown once).
    /// </summary>
    /// <exception cref="AccountApiException">A non-200 answer (400 <c>/errors/dek-invalid</c>, 401, 403, …).</exception>
    /// <exception cref="HttpRequestException">Transport failure.</exception>
    public async Task<UnlockResponse> UnlockAsync(string dek, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dek);

        using var response = await http
            .PostAsJsonAsync(UnlockPath, new UnlockRequest { Dek = dek }, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await AccountApiException.FromAsync(response, ct).ConfigureAwait(false);
        }

        var body = await response.Content
            .ReadFromJsonAsync<UnlockResponse>(cancellationToken: ct)
            .ConfigureAwait(false);
        if (body is null || string.IsNullOrWhiteSpace(body.CryptoSessionToken))
        {
            throw new AccountApiException(HttpStatusCode.OK, null,
                "The unlock response did not carry a session token.");
        }

        return body;
    }
}

/// <summary>200 body of <c>GET /v1/account/kdf</c> — base64/JSON wire form.</summary>
public sealed class KdfMaterialResponse
{
    [JsonPropertyName("kdf_salt")]
    public string KdfSalt { get; init; } = string.Empty;

    [JsonPropertyName("kdf_params")]
    public JsonElement KdfParams { get; init; }

    [JsonPropertyName("dek_wrapped")]
    public string DekWrapped { get; init; } = string.Empty;

    [JsonPropertyName("dek_nonce")]
    public string DekNonce { get; init; } = string.Empty;

    /// <summary>Base64-decodes the material for <see cref="IUnlockCrypto"/>.</summary>
    /// <exception cref="FormatException">A field is not base64.</exception>
    public KdfMaterial ToMaterial()
        => new(
            Convert.FromBase64String(KdfSalt),
            KdfParams.Deserialize<KdfParamsDto>() ?? throw new FormatException(
                "kdf_params is not a KDF parameter object."),
            Convert.FromBase64String(DekWrapped),
            Convert.FromBase64String(DekNonce));
}

/// <summary>
/// The decoded <c>GET /v1/account/kdf</c> material the unlock service feeds
/// to <see cref="IUnlockCrypto"/>: salt bytes, the stored Argon2id cost
/// parameters, and the KEK-wrapped DEK ciphertext + nonce.
/// </summary>
public sealed record KdfMaterial(
    byte[] KdfSalt,
    KdfParamsDto KdfParams,
    byte[] DekWrapped,
    byte[] DekNonce);

/// <summary>
/// Mirror of the API's <c>KdfParams</c> jsonb shape (<c>users.kdf_params</c>,
/// M1T3 / Q-E2) — explicit member names so the round-trip is policy-free.
/// </summary>
public sealed class KdfParamsDto
{
    [JsonPropertyName("memory_kb")]
    public int MemoryKb { get; init; }

    [JsonPropertyName("iterations")]
    public int Iterations { get; init; }

    [JsonPropertyName("parallelism")]
    public int Parallelism { get; init; }

    [JsonPropertyName("hash_length")]
    public int HashLength { get; init; }
}

/// <summary>Body of <c>POST /v1/account/unlock</c> — base64 of the 32-byte DEK.</summary>
public sealed class UnlockRequest
{
    [JsonPropertyName("dek")]
    public string? Dek { get; init; }
}

/// <summary>200 body of <c>POST /v1/account/unlock</c> — the token is emitted exactly once.</summary>
public sealed class UnlockResponse
{
    [JsonPropertyName("crypto_session_id")]
    public Guid CryptoSessionId { get; init; }

    [JsonPropertyName("crypto_session_token")]
    public string CryptoSessionToken { get; init; } = string.Empty;
}

/// <summary>
/// A non-200 (or unusable) answer from the account unlock flow, carrying the
/// RFC 7807 <c>type</c>/<c>detail</c> so <see cref="Security.UnlockService"/>
/// can map it to a user-facing outcome. Never carries key material.
/// </summary>
public sealed class AccountApiException : Exception
{
    public AccountApiException(HttpStatusCode status, string? problemType, string? detail,
        Exception? inner = null)
        : base(detail ?? $"unlock flow failed with status {(int)status}.", inner)
    {
        Status = status;
        ProblemType = problemType;
        Detail = detail;
    }

    /// <summary>The HTTP status the flow failed with.</summary>
    public HttpStatusCode Status { get; }

    /// <summary>The RFC 7807 <c>type</c> (e.g. <c>/errors/passphrase-not-set</c>), when the body was a problem document.</summary>
    public string? ProblemType { get; }

    /// <summary>The problem <c>detail</c>, when present.</summary>
    public string? Detail { get; }

    /// <summary>Reads a non-success response as a typed exception (problem document when the body is one).</summary>
    public static async Task<AccountApiException> FromAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        string? type = null;
        string? detail = null;
        try
        {
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var problem = JsonSerializer.Deserialize<ApiProblem>(
                    raw, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                type = problem?.Type;
                detail = problem?.Detail ?? problem?.Title;
            }
        }
        catch (JsonException)
        {
            // non-problem body — status alone decides downstream
        }
        catch (IOException)
        {
            // unreadable body — status alone decides downstream
        }

        return new AccountApiException(response.StatusCode, type, detail);
    }
}
