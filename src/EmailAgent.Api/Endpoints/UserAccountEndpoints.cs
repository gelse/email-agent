using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmailAgent.Api.Errors;
using EmailAgent.Core.Audit;
using EmailAgent.Core.Identity;
using EmailAgent.Core.Security;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;

namespace EmailAgent.Api.Endpoints;

/// <summary>
/// M2T5 addition (task file "KDF endpoint"): <c>GET /v1/user/account/kdf</c>.
/// M2T6 addition (adjudication R2): <c>POST /v1/user/account/unwrap-dek</c>.
/// </summary>
/// <remarks>
/// <para>
/// The M2 sources endpoints <b>used to</b> need the caller's data-encryption
/// key in the <c>X-User-Dek</c> header (SQ-M2-1 / §13.16) — that transport was
/// removed in M5T1 (§13.17 / Q-E9), so these routes no longer carry a DEK
/// header of any kind. <c>GET /kdf</c> returns the account's Argon2id salt and
/// parameters for the signed-in caller only: no wrapped DEK, no nonce, no
/// other account's material.
/// </para>
/// <para>
/// As of M2T6 the SPA does <b>not</b> derive the KEK in the browser: it posts
/// the passphrase to <c>POST /unwrap-dek</c>, which runs
/// <see cref="IEncryptionService.UnwrapDek"/> server-side for the signed-in
/// caller and returns the plaintext DEK (M2-only, SQ-12/Q-E9 — the M2T6 task
/// file's R2 adjudication supersedes the earlier in-browser-derivation plan;
/// M5T1 later revives client-side derivation under §13.17, which is why the
/// read-only <c>GET /kdf</c> endpoint stays as shipped by M2T5). Neither
/// endpoint logs or audits the passphrase, the KEK or the DEK.
/// </para>
/// <para>
/// A caller that has never set a passphrase has no salt and no wrapped DEK —
/// both endpoints answer 409 <c>/errors/passphrase-not-set</c> so the SPA can
/// run the first-login passphrase flow instead. The namespace is distinct from
/// M5T1's later <c>/v1/account/kdf</c> (different URL-segment namespace; Q-E9b
/// keeps the M5 surface under <c>/v1/account</c>), so neither task overwrites
/// the other.
/// </para>
/// </remarks>
public static class UserAccountEndpoints
{
    /// <summary>§13.12 error classes this namespace emits.</summary>
    private static class ErrorTypes
    {
        public const string Validation = "/errors/validation";
        public const string Unauthorized = "/errors/unauthorized";
        public const string Forbidden = "/errors/forbidden";
        public const string PassphraseNotSet = "/errors/passphrase-not-set";
        public const string RateLimited = "/errors/rate-limited";
        public const string CredentialMismatch = "/errors/credential-mismatch";
    }

    /// <summary>Maps the M2 user-account routes (<c>/kdf</c>, <c>/unwrap-dek</c>).</summary>
    public static IEndpointRouteBuilder MapUserAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/v1/user/account")
            .WithTags("UserAccount")
            .RequireAuthorization();

        group.MapGet("/kdf", Kdf).WithName("GetUserKdf");

        group.MapPost("/unwrap-dek", UnwrapDek).WithName("PostUserUnwrapDek");

        return endpoints;
    }

    /// <summary>
    /// <c>GET /v1/user/account/kdf</c> — the caller's base64 KDF salt plus the
    /// Argon2id parameters stored for the account.
    /// </summary>
    /// <remarks>
    /// M2T5 shipped this as the SPA's input for in-browser KEK derivation;
    /// M2T6's <c>POST /unwrap-dek</c> replaced that flow (the browser now sends
    /// the passphrase to the server instead of deriving locally, SQ-12/Q-E9).
    /// The read itself is unchanged — M5T1's client-side derivation returns
    /// against this contract — and, like every read here, it is not audited.
    /// </remarks>
    private static async Task<IResult> Kdf(
        HttpContext ctx, UserManager<User> userManager, CancellationToken ct)
    {
        var user = await userManager.GetUserAsync(ctx.User);
        if (user is null)
        {
            return ProblemDetailsHelpers.Problem(
                ctx,
                StatusCodes.Status401Unauthorized,
                ErrorTypes.Unauthorized,
                "Unauthorized",
                "The access token does not match a live account.");
        }

        if (user.Status == UserStatus.Disabled)
        {
            return ProblemDetailsHelpers.Problem(
                ctx,
                StatusCodes.Status403Forbidden,
                ErrorTypes.Forbidden,
                "Forbidden",
                "This account is disabled.");
        }

        if (user.KdfSalt is not { Length: > 0 } salt)
        {
            return ProblemDetailsHelpers.Problem(
                ctx,
                StatusCodes.Status409Conflict,
                ErrorTypes.PassphraseNotSet,
                "Conflict",
                "No passphrase has been set for this account, so no key-derivation "
                + "parameters exist yet. Complete the first-login passphrase flow.");
        }

        // Clone before the entity's JsonDocument can be disposed — the element
        // must outlive this request scope.
        var kdfParams = user.KdfParams is { } document
            ? document.RootElement.Clone()
            : JsonSerializer.SerializeToDocument(
                KdfParams.Default()).RootElement.Clone();

        // Account material (M2T6 review fix): the salt+parameters pair is the
        // input to the unwrap below — never a shared cache's payload.
        ctx.Response.Headers.CacheControl = "no-store";

        return Results.Json(
            new KdfResponse
            {
                KdfSalt = Convert.ToBase64String(salt),
                KdfParams = kdfParams,
            },
            UserAccountJsonContext.Default,
            statusCode: StatusCodes.Status200OK);
    }

    /// <summary>
    /// <c>POST /v1/user/account/unwrap-dek</c> — M2T6 (adjudication R2):
    /// body <c>{ "passphrase": "…" }</c> → 200 <c>{ "dek": "&lt;base64&gt;" }</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server derives the KEK and unwraps the caller's own wrapped DEK
    /// (<see cref="IEncryptionService.UnwrapDek"/>) — M2-only (SQ-12/Q-E9);
    /// M5T1's §13.17 in-browser derivation supersedes this endpoint, and the
    /// M2T6 task file records that supersession. The M2 SPA cached the result
    /// in memory and sent it as the <c>X-User-Dek</c> header — removed with
    /// that transport in M5T1 — and it was never persisted in the browser;
    /// neither this handler nor any logger ever records the
    /// passphrase, the KEK or the DEK. The success body carries
    /// <c>Cache-Control: no-store</c> and the plaintext byte buffer is zeroed
    /// once encoded (review fixes).
    /// </para>
    /// <para>
    /// Mappings, in handler order: no live account → 401; disabled → 403; no
    /// stored key material → 409 <c>/errors/passphrase-not-set</c>; missing,
    /// malformed, empty or over-size (4 KiB) body → 400
    /// <c>/errors/validation</c> (field <c>passphrase</c>); a derivation
    /// already in flight → 429 with
    /// <c>Retry-After: 1</c>; a passphrase that does not authenticate the wrap
    /// → audited <c>dek_unwrap_failed</c> then 401
    /// <c>/errors/credential-mismatch</c>. Success is not audited.
    /// </para>
    /// </remarks>
    private static async Task<IResult> UnwrapDek(
        HttpContext ctx,
        UserManager<User> userManager,
        IEncryptionService encryptionService,
        IAuditLogger auditLogger,
        CancellationToken ct)
    {
        var user = await userManager.GetUserAsync(ctx.User);
        if (user is null)
        {
            return ProblemDetailsHelpers.Problem(
                ctx,
                StatusCodes.Status401Unauthorized,
                ErrorTypes.Unauthorized,
                "Unauthorized",
                "The access token does not match a live account.");
        }

        // A still-valid JWT must not keep working for a Disabled account
        // (status gate parity with set-passphrase / change-password).
        if (user.Status == UserStatus.Disabled)
        {
            return ProblemDetailsHelpers.Problem(
                ctx,
                StatusCodes.Status403Forbidden,
                ErrorTypes.Forbidden,
                "Forbidden",
                "This account is disabled.");
        }

        // Sentinel before the body: an account with no key material cannot
        // unwrap anything, so the SPA is told to run the first-login
        // passphrase flow regardless of what it posted (Q-E6: the four columns
        // are written together, so any gap means "no passphrase yet").
        if (user.DekWrapped is not { Length: > 0 } dekWrapped
            || user.KdfSalt is not { Length: > 0 } salt
            || user.DekNonce is not { Length: 12 } dekNonce)
        {
            return ProblemDetailsHelpers.Problem(
                ctx,
                StatusCodes.Status409Conflict,
                ErrorTypes.PassphraseNotSet,
                "Conflict",
                "No passphrase has been set for this account, so no wrapped data-encryption "
                + "key exists yet. Complete the first-login passphrase flow.");
        }

        var kdfParams = ResolveKdfParams(user);

        var (request, bodyError) = await ReadPassphraseBodyAsync(ctx, ct);
        if (bodyError is not null || request is null)
        {
            return bodyError!;
        }

        byte[] dek;
        try
        {
            // Q-E5: the per-user zero-wait KEK mutex inside the service — a
            // concurrent derivation for the same user throws and maps to the
            // 429 below; Q-E9: only the passphrase holder can produce the DEK.
            dek = encryptionService.UnwrapDek(
                user.Id, request.Passphrase!, salt, kdfParams, dekWrapped, dekNonce);
        }
        catch (KekDerivationBusyException)
        {
            ctx.Response.Headers.RetryAfter = "1";
            return ProblemDetailsHelpers.Problem(
                ctx,
                StatusCodes.Status429TooManyRequests,
                ErrorTypes.RateLimited,
                "Too Many Requests",
                "A key derivation for this account is already in progress; retry shortly.");
        }
        catch (InvalidPassphraseException)
        {
            // Failure-only audit (M2T6): the row mirrors the login_failure
            // shape and its metadata is empty — never the passphrase, KEK or
            // DEK (Q-E9). Written before the 401 so a probe leaves evidence.
            await auditLogger.LogAsync(
                new AuditEvent(
                    ActorUserId: user.Id,
                    ActorRole: user.Role.ToString(),
                    Action: AuditActions.DekUnwrapFailed,
                    TargetType: "user",
                    TargetId: user.Id.ToString(),
                    MetadataJson: "{}",
                    Ip: ctx.Connection.RemoteIpAddress),
                ct);

            return ProblemDetailsHelpers.Problem(
                ctx,
                StatusCodes.Status401Unauthorized,
                ErrorTypes.CredentialMismatch,
                "Unauthorized",
                "The passphrase is not valid.");
        }

        try
        {
            // The plaintext key exists in one response body only (M2T6 review
            // fixes): never cacheable, and the byte buffer is zeroed as soon
            // as its base64 wire form is built — same hygiene as the sources
            // handlers (SourcesHandlers.Zero). Serialisation reads the string
            // record, not this array, so zeroing here cannot blank the body.
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Json(
                new UnwrapDekResponse
                {
                    Dek = Convert.ToBase64String(dek),
                },
                UserAccountJsonContext.Default,
                statusCode: StatusCodes.Status200OK);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>
    /// <c>users.kdf_params</c> → <see cref="KdfParams"/>; a null column (or an
    /// unparseable document) falls back to the Q-E2 defaults, exactly as the
    /// set-passphrase writer always persists them. A fallback that does not
    /// match the stored wrap simply fails GCM authentication below (401), it
    /// never turns into a 500.
    /// </summary>
    private static KdfParams ResolveKdfParams(User user)
    {
        if (user.KdfParams is not { } document)
        {
            return KdfParams.Default();
        }

        try
        {
            return document.RootElement.Deserialize<KdfParams>() ?? KdfParams.Default();
        }
        catch (JsonException)
        {
            return KdfParams.Default();
        }
    }

    /// <summary>
    /// Reads <c>{ "passphrase": "…" }</c> manually, mirroring M1T6's
    /// <c>ReadJsonBodyAsync</c> (a missing or malformed payload must be a
    /// §13.12 400 problem document, not the framework binder's empty-bodied
    /// rejection). Every rejection — absent body, malformed JSON, an
    /// over-size body, or a null/empty/whitespace value — reports the field
    /// name <c>passphrase</c>. The read is bounded by
    /// <see cref="MaxPassphraseBodyBytes"/> (M2T6 review fix): a passphrase is
    /// never kilobytes, so a multi-megabyte POST must not be buffered whole
    /// into a string while the per-user KEK mutex is held. Anything within
    /// the bound that is present still runs the unwrap below and lands on the
    /// uniform 401 credential mismatch.
    /// </summary>
    private static async Task<(UnwrapDekRequest? Body, IResult? Error)> ReadPassphraseBodyAsync(
        HttpContext ctx,
        CancellationToken ct)
    {
        if (ctx.Request.ContentLength is > MaxPassphraseBodyBytes)
        {
            return (null, BodyTooLarge(ctx));
        }

        var builder = new StringBuilder();
        var chunk = new char[1024];
        using (var reader = new StreamReader(
            ctx.Request.Body, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
        {
            int read;
            while ((read = await reader.ReadAsync(chunk.AsMemory(), ct)) > 0)
            {
                if (builder.Length + read > MaxPassphraseBodyBytes)
                {
                    return (null, BodyTooLarge(ctx));
                }

                builder.Append(chunk, 0, read);
            }
        }

        var raw = builder.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, PassphraseRequired(ctx));
        }

        UnwrapDekRequest? body;
        try
        {
            body = JsonSerializer.Deserialize<UnwrapDekRequest>(raw, BodySerializerOptions);
        }
        catch (JsonException)
        {
            return (null, ValidationProblem(
                ctx, "passphrase", "The request body is not valid JSON."));
        }

        if (body is null || string.IsNullOrWhiteSpace(body.Passphrase))
        {
            return (null, PassphraseRequired(ctx));
        }

        return (body, null);
    }

    private static IResult PassphraseRequired(HttpContext ctx)
        => ValidationProblem(ctx, "passphrase", "A passphrase is required.");

    /// <summary>Hard cap on the whole unwrap body (M2T6 review fix) — a passphrase is never kilobytes.</summary>
    private const int MaxPassphraseBodyBytes = 4096;

    private static IResult BodyTooLarge(HttpContext ctx)
        => ValidationProblem(ctx, "passphrase", "The request body is too large.");

    private static IResult ValidationProblem(HttpContext ctx, string name, string reason)
        => ProblemDetailsHelpers.Problem(
            ctx,
            StatusCodes.Status400BadRequest,
            ErrorTypes.Validation,
            "Validation failed",
            "One or more fields are invalid.",
            new[] { (name, reason) });

    private static readonly JsonSerializerOptions BodySerializerOptions =
        new(JsonSerializerDefaults.Web);
}

/// <summary>Body of <c>GET /v1/user/account/kdf</c> — never any wrapped key material.</summary>
public sealed record KdfResponse
{
    /// <summary>Base64 of the per-account Argon2id salt (<c>users.kdf_salt</c>).</summary>
    [JsonPropertyName("kdf_salt")]
    public string KdfSalt { get; init; } = string.Empty;

    /// <summary>The Argon2id parameters (<c>users.kdf_params</c>) as stored.</summary>
    [JsonPropertyName("kdf_params")]
    public JsonElement KdfParams { get; init; }
}

/// <summary>Body of <c>POST /v1/user/account/unwrap-dek</c> (M2T6, R2).</summary>
public sealed record UnwrapDekRequest
{
    /// <summary>The account passphrase; required, and the whole body is capped at 4 KiB.</summary>
    [JsonPropertyName("passphrase")]
    public string? Passphrase { get; init; }
}

/// <summary>Body of <c>200 OK</c> from <c>POST /v1/user/account/unwrap-dek</c>.</summary>
public sealed record UnwrapDekResponse
{
    /// <summary>Base64 of the 32-byte plaintext DEK — sent once, never logged.</summary>
    [JsonPropertyName("dek")]
    public string Dek { get; init; } = string.Empty;
}

/// <summary>Source-gen context for the user-account bodies (AOT-friendly, task step 8 rule).</summary>
[JsonSerializable(typeof(KdfResponse))]
[JsonSerializable(typeof(UnwrapDekRequest))]
[JsonSerializable(typeof(UnwrapDekResponse))]
public partial class UserAccountJsonContext : JsonSerializerContext
{
}
