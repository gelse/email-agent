using System.Text.Json;
using System.Text.Json.Serialization;
using EmailAgent.Api.Errors;
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
/// </summary>
/// <remarks>
/// <para>
/// The M2 sources endpoints need the caller's data-encryption key in the
/// <c>X-User-Dek</c> header (SQ-M2-1), and the only way the browser can
/// produce it is by deriving the KEK from the passphrase — which needs the
/// account's Argon2id salt and parameters. M1 exposes neither (its passphrase
/// flows are entirely server-side), so this read-only endpoint returns exactly
/// those two values for the <b>signed-in caller only</b>: no wrapped DEK, no
/// nonce, no other account's material.
/// </para>
/// <para>
/// A caller that has never set a passphrase has no salt to return — 409
/// <c>/errors/passphrase-not-set</c> tells the SPA to run the first-login
/// passphrase flow instead. The endpoint is distinct from M5T1's later
/// <c>GET /v1/account/kdf</c> (different URL-segment namespace; Q-E9b keeps
/// the M5 surface under <c>/v1/account</c>), so neither task overwrites the
/// other. It is a read and, like every other read here, is not audited.
/// </para>
/// </remarks>
public static class UserAccountEndpoints
{
    /// <summary>§13.12 error classes this namespace emits.</summary>
    private static class ErrorTypes
    {
        public const string Unauthorized = "/errors/unauthorized";
        public const string Forbidden = "/errors/forbidden";
        public const string PassphraseNotSet = "/errors/passphrase-not-set";
    }

    /// <summary>Maps the M2T5 user-account routes (currently <c>/kdf</c> only).</summary>
    public static IEndpointRouteBuilder MapUserAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/v1/user/account")
            .WithTags("UserAccount")
            .RequireAuthorization();

        group.MapGet("/kdf", Kdf).WithName("GetUserKdf");

        return endpoints;
    }

    /// <summary>
    /// <c>GET /v1/user/account/kdf</c> — the caller's base64 KDF salt plus the
    /// Argon2id parameters the SPA must use to derive the KEK (and from it the
    /// DEK it then sends as <c>X-User-Dek</c>).
    /// </summary>
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

        return Results.Json(
            new KdfResponse
            {
                KdfSalt = Convert.ToBase64String(salt),
                KdfParams = kdfParams,
            },
            UserAccountJsonContext.Default,
            statusCode: StatusCodes.Status200OK);
    }
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

/// <summary>Source-gen context for the user-account responses (AOT-friendly, task step 8 rule).</summary>
[JsonSerializable(typeof(KdfResponse))]
public partial class UserAccountJsonContext : JsonSerializerContext
{
}
