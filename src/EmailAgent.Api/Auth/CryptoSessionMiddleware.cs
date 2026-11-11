using EmailAgent.Core.Security;
using EmailAgent.Api.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace EmailAgent.Api.Auth;

/// <summary>
/// M5T1 (SQ-M5-1 / §13.17): the generic 401-vs-403 enforcement point for the
/// whole <c>/v1/user/*</c> namespace — the same guard every user-data
/// operation shares (pipeline, dispatcher, body read: this is not
/// pipeline-specific). For a request under <c>/v1/user/</c> that carries an
/// authenticated principal, the crypto session must be valid; otherwise the
/// request short-circuits with an RFC 7807 <b>403</b>
/// <c>/errors/crypto-locked</c> document. A success sliding-touches the idle
/// timer, so activity refreshes the session.
/// </summary>
/// <remarks>
/// <para>
/// The 401 half of the split is untouched: an absent or expired JWT is
/// refused by the bearer handler / authorization middleware <b>before</b>
/// this middleware runs (it is registered after
/// <c>UseAuthentication</c>/<c>UseAuthorization</c>), so "no credentials" and
/// "locked vault" can never collapse into one status (SQ-M5-1's rationale for
/// keeping the crypto session out of the JWT claims).
/// </para>
/// <para>
/// Placement after <see cref="AuthenticatedRateLimitMiddleware"/> means the
/// §13.14 <c>X-RateLimit-*</c> headers still wrap this 403 like every other
/// authenticated response.
/// </para>
/// <para>
/// Requests without an authenticated principal are passed through — such
/// routes are the account/admin namespaces (not guarded) or a user route
/// whose own authorization already produced (or will produce) the 401.
/// </para>
/// </remarks>
public sealed class CryptoSessionMiddleware
{
    /// <summary>The §13.12 error class of the locked-vault 403 (SQ-M5-1).</summary>
    public const string CryptoLockedErrorType = "/errors/crypto-locked";

    /// <summary>The namespace this guard is generic across (M5T6/M5T7 inherit it).</summary>
    public const string UserPathPrefix = "/v1/user";

    private readonly RequestDelegate _next;

    public CryptoSessionMiddleware(RequestDelegate next)
        => _next = next ?? throw new ArgumentNullException(nameof(next));

    public async Task InvokeAsync(HttpContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (!ctx.Request.Path.StartsWithSegments(UserPathPrefix)
            || ctx.User.Identity?.IsAuthenticated != true)
        {
            await _next(ctx);
            return;
        }

        var accessor = ctx.RequestServices.GetRequiredService<ICryptoSessionAccessor>();
        if (!accessor.IsUnlocked)
        {
            await ProblemDetailsHelpers.Problem(
                    ctx,
                    StatusCodes.Status403Forbidden,
                    CryptoLockedErrorType,
                    "Crypto session locked",
                    "The data-encryption vault for this session is locked or expired. "
                    + "Unlock with the account passphrase and retry.")
                .ExecuteAsync(ctx);
            return;
        }

        // Sliding idle touch: a successful user-data request refreshes
        // SESSION__IdleTimeout (never past the absolute cap — the store clamps).
        accessor.Touch();

        await _next(ctx);
    }
}

/// <summary>DI/pipeline entry point for <see cref="CryptoSessionMiddleware"/> (M5T1 task step 10).</summary>
public static class CryptoSessionMiddlewareExtensions
{
    /// <summary>
    /// Guards <c>/v1/user/*</c> with the 403 <c>/errors/crypto-locked</c>
    /// contract. Call after <c>UseAuthentication</c>/<c>UseAuthorization</c>
    /// and after the authenticated rate-limit middleware (§13.14 headers).
    /// </summary>
    public static IApplicationBuilder UseCryptoSession(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<CryptoSessionMiddleware>();
    }
}
