using EmailAgent.Api.Errors;
using EmailAgent.Api.Models;
using EmailAgent.Api.Pagination;
using EmailAgent.Core.Identity;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace EmailAgent.Api.Endpoints;

// ---------------------------------------------------------------------------
// M3T7: the HTTP half of GET /v1/user/models. Each step mirrors
// AdminModelsHandlers (actor → query-shape → service), minus every write: the
// endpoint is read-only, so it emits no audit row and touches no secret.
// Rate-limit headers and the role gate come from the pipeline (§13.14 /
// §13.3), never from here.
// ---------------------------------------------------------------------------

/// <summary>Route handlers for <see cref="UserModelEndpoints"/>.</summary>
public static class UserModelsHandlers
{
    /// <summary>§13.12 error classes this namespace emits.</summary>
    private static class ErrorTypes
    {
        public const string Validation = "/errors/validation";
        public const string Unauthorized = "/errors/unauthorized";
        public const string Forbidden = "/errors/forbidden";
    }

    /// <summary>
    /// <c>GET /v1/user/models</c> — the §11 model allow-list as a reduced
    /// projection: <c>enabled = true</c> AND
    /// <c>tool_calling_probe_result = Pass</c>, capped by
    /// <c>?limit=</c> (default 25, max 100). Not audited (Q-API44 — reads are
    /// not state changes, M1T7/M2T5/M3T3 list parity).
    /// </summary>
    internal static async Task<IResult> List(
        HttpContext ctx,
        UserModelService service,
        UserManager<User> userManager,
        CancellationToken ct)
    {
        var actorError = await ResolveActorErrorAsync(ctx, userManager, ct);
        if (actorError is not null)
        {
            return actorError;
        }

        if (!PaginationQuery.TryParseLimit(
                ctx.Request.Query["limit"].ToString(), out var limit, out var limitError))
        {
            return ValidationProblem(ctx, new[] { ("limit", limitError!) });
        }

        try
        {
            var items = await service.ListPickableAsync(limit, ct);
            return Results.Json(
                new UserModelListResponse { Items = items.ToList() },
                UserModelsJsonContext.Default,
                statusCode: StatusCodes.Status200OK);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException)
        {
            // Belt-and-braces: TryParseLimit already rejects an out-of-range
            // value, so this only fires if a future caller bypasses it — it
            // must still be a §13.12 400, never an unhandled 500.
            return ValidationProblem(ctx, new[] { ("limit", exception.Message) });
        }
    }

    // -----------------------------------------------------------------------
    // Actor plumbing (M1T7/M2T5/M3T3 parity)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolves the caller through <c>UserManager</c>: no live account → 401,
    /// Disabled → 403 (a still-valid JWT must not keep reading power for a
    /// blocked account). Returns <c>null</c> when the caller is usable.
    /// </summary>
    private static async Task<IResult?> ResolveActorErrorAsync(
        HttpContext ctx, UserManager<User> userManager, CancellationToken ct)
    {
        var user = await userManager.GetUserAsync(ctx.User);
        if (user is null)
        {
            return Problem(
                ctx,
                StatusCodes.Status401Unauthorized,
                ErrorTypes.Unauthorized,
                "Unauthorized",
                "The access token does not match a live account.");
        }

        if (user.Status == UserStatus.Disabled)
        {
            return Problem(
                ctx,
                StatusCodes.Status403Forbidden,
                ErrorTypes.Forbidden,
                "Forbidden",
                "This account is disabled.");
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // Problem documents (§13.12)
    // -----------------------------------------------------------------------

    private static IResult Problem(
        HttpContext ctx,
        int status,
        string type,
        string title,
        string? detail = null,
        IReadOnlyList<(string Name, string Reason)>? errors = null)
        => ProblemDetailsHelpers.Problem(ctx, status, type, title, detail, errors);

    private static IResult ValidationProblem(
        HttpContext ctx,
        IReadOnlyList<(string Name, string Reason)> errors)
        => Problem(
            ctx,
            StatusCodes.Status400BadRequest,
            ErrorTypes.Validation,
            "Validation failed",
            "One or more fields are invalid.",
            errors);
}
