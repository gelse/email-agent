using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace EmailAgent.Api.Endpoints;

/// <summary>
/// M3T7 / §5+§13.3: the <c>GET /v1/user/models</c> route under the §13.7
/// <c>/v1</c> prefix (SQ-M3-6 / SQ-M3-12). The group is
/// <c>[Authorize(Roles = "User, Admin")]</c> — any authenticated, non-disabled
/// user may list the models they can pick from; anonymous callers get a 401
/// problem document and the role gate a 403 one from the JwtBearer
/// challenge/forbid events wired in Program.cs (M1T7 parity), with no
/// endpoint-local auth code.
/// </summary>
/// <remarks>
/// <c>GET</c> maps the collection root as <c>string.Empty</c> (the
/// user/admin namespace spelling): the group prefix already carries
/// <c>/v1/user/models</c>, so the canonical URL without a trailing slash is
/// the one that matches. Rate limiting and the §13.14 <c>X-RateLimit-*</c>
/// headers are inherited from the pipeline (the per-user 60/min budget rides
/// <see cref="Auth.RateLimitPolicies.AuthenticatedPerUser"/> /
/// <c>AuthenticatedRateLimitMiddleware</c>) — no per-endpoint policy is
/// attached. The response is deliberately a reduced projection; the
/// allow-list filter lives server-side in <see cref="Models.UserModelService"/>,
/// never in the SPA.
/// </remarks>
public static class UserModelEndpoints
{
    /// <summary>Maps <c>GET /v1/user/models</c> and returns the builder.</summary>
    public static IEndpointRouteBuilder MapUserModelEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/v1/user/models")
            .WithTags("UserModels")
            .RequireAuthorization(policy => policy.RequireRole("User", "Admin"));

        group.MapGet(string.Empty, UserModelsHandlers.List).WithName("ListUserModels");

        return endpoints;
    }
}
