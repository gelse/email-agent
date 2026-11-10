using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace EmailAgent.Api.Endpoints;

/// <summary>
/// M3T3 / §5+§13.3: the five <c>/v1/admin/models</c> routes under the §13.7
/// <c>/v1</c> prefix — list, create, read, patch and delete of the model
/// catalog. The whole group is <c>[Authorize(Roles = "Admin")]</c> (Q-L1 /
/// SQ-M3-1): anonymous callers get a 401 problem document and authenticated
/// non-admins a 403 one from the JwtBearer challenge/forbid events wired in
/// Program.cs (M1T7 parity), with no endpoint-local auth code.
/// </summary>
/// <remarks>
/// <c>GET</c> maps the collection root as <c>string.Empty</c> (the admin
/// namespace's spelling): the group prefix already carries
/// <c>/v1/admin/models</c>, so the canonical URL without a trailing slash is
/// the one that matches. Rate limiting and the §13.14 <c>X-RateLimit-*</c>
/// headers are inherited from the pipeline (the per-user 60/min budget rides
/// <see cref="Auth.RateLimitPolicies.AuthenticatedPerUser"/> /
/// <c>AuthenticatedRateLimitMiddleware</c>) — no per-endpoint policy is
/// attached, so every authenticated response that reaches the middleware
/// carries them; the JWT challenge/forbid short-circuit at
/// <c>UseAuthorization</c> runs earlier and answers without them (pre-existing
/// M1T4 pipeline order).
/// The test-completion endpoint of the task's scope table is M3T6's, not
/// mapped here.
/// </remarks>
public static class AdminModelEndpoints
{
    /// <summary>The single role the whole namespace requires (§13.3 / SQ-M3-1).</summary>
    private const string AdminRole = "Admin";

    /// <summary>Maps the five catalog routes (task step 4) and returns the builder.</summary>
    public static IEndpointRouteBuilder MapAdminModelEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/v1/admin/models")
            .WithTags("Admin")
            .RequireAuthorization(policy => policy.RequireRole(AdminRole));

        group.MapGet(string.Empty, AdminModelsHandlers.List).WithName("AdminListModels");
        group.MapPost(string.Empty, AdminModelsHandlers.Create).WithName("AdminCreateModel");
        group.MapGet("/{id:guid}", AdminModelsHandlers.Get).WithName("AdminGetModel");
        group.MapPatch("/{id:guid}", AdminModelsHandlers.Update).WithName("AdminUpdateModel");
        group.MapDelete("/{id:guid}", AdminModelsHandlers.Delete).WithName("AdminDeleteModel");

        return endpoints;
    }
}
