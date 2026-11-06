using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace EmailAgent.Api.Endpoints;

/// <summary>
/// M2T5 / §5+§13.3: the seven <c>/v1/user/sources</c> routes under the §13.7
/// <c>/v1</c> prefix. The whole group is <c>[Authorize(Roles = "User")]</c> —
/// anonymous callers get a 401 problem document and authenticated non-users a
/// 403 one from the JwtBearer challenge/forbid events wired in Program.cs,
/// with no endpoint-local auth code (M1T7 parity).
/// </summary>
/// <remarks>
/// <c>GET</c> maps the collection root as <c>string.Empty</c> (the admin
/// namespace's spelling): the group prefix already carries
/// <c>/v1/user/sources</c>, so the canonical URL without a trailing slash is
/// the one that matches. Rate limiting and the §13.14 <c>X-RateLimit-*</c>
/// headers are inherited from the pipeline (the per-user 60/min budget rides
/// <see cref="Auth.RateLimitPolicies.AuthenticatedPerUser"/> /
/// <c>AuthenticatedRateLimitMiddleware</c>) — no per-endpoint policy is
/// attached, so every authenticated response in this namespace carries them.
/// </remarks>
public static class SourcesEndpoints
{
    /// <summary>The single role the whole namespace requires (§13.3).</summary>
    private const string UserRoleName = "User";

    /// <summary>Maps the seven source routes (task step 6) and returns the builder.</summary>
    public static IEndpointRouteBuilder MapSourcesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/v1/user/sources")
            .WithTags("Sources")
            .RequireAuthorization(policy => policy.RequireRole(UserRoleName));

        group.MapGet(string.Empty, SourcesHandlers.List).WithName("ListSources");
        group.MapPost(string.Empty, SourcesHandlers.Create).WithName("CreateSource");
        group.MapGet("/{id:guid}", SourcesHandlers.Get).WithName("GetSource");
        group.MapPatch("/{id:guid}", SourcesHandlers.Update).WithName("UpdateSource");
        group.MapDelete("/{id:guid}", SourcesHandlers.Delete).WithName("DeleteSource");

        // Literal segment, so it outranks {id:guid} even though both are POST.
        group.MapPost("/test-connection", SourcesHandlers.TestConnection)
            .WithName("TestSourceConnection");
        group.MapPost("/{id:guid}/fetch-now", SourcesHandlers.FetchNow)
            .WithName("FetchSourceNow");

        return endpoints;
    }
}
