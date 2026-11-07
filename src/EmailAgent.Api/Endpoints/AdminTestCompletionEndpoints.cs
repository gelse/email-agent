using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace EmailAgent.Api.Endpoints;

/// <summary>
/// M3T6 / §13.3 / SQ-M3-16: <c>POST /v1/admin/models/{id}/test-completion</c>
/// — the manual test-completion tool under the §13.7 <c>/v1</c> prefix. The
/// group is <c>[Authorize(Roles = "Admin")]</c> (SQ-M3-1): anonymous callers
/// get a 401 problem document and authenticated non-admins a 403 one from the
/// JwtBearer challenge/forbid events wired in Program.cs (M1T7 parity), with
/// no endpoint-local auth code.
/// </summary>
/// <remarks>
/// The route lives in its OWN group (and file) rather than M3T3's
/// <c>/v1/admin/models</c> group because the path adds a literal
/// <c>test-completion</c> segment under <c>{id}</c> — a separate group keeps
/// the M3T3 mapping untouched (task Files affected). The POST maps the group
/// root as <c>string.Empty</c>: the group prefix already carries the full
/// canonical URL, so the route matches without a trailing slash (M3T3's
/// spelling). Rate limiting and the §13.14 <c>X-RateLimit-*</c> headers are
/// inherited from the pipeline (the per-user 60/min budget rides
/// <see cref="Auth.RateLimitPolicies.AuthenticatedPerUser"/> /
/// <c>AuthenticatedRateLimitMiddleware</c>) — no per-endpoint policy is
/// attached, so every authenticated response that reaches the middleware
/// carries them; the JWT challenge/forbid short-circuit at
/// <c>UseAuthorization</c> runs earlier and answers without them (pre-existing
/// M1T4 pipeline order).
/// </remarks>
public static class AdminTestCompletionEndpoints
{
    /// <summary>The single role the whole namespace requires (§13.3 / SQ-M3-1).</summary>
    private const string AdminRole = "Admin";

    /// <summary>Maps <c>POST /v1/admin/models/{id}/test-completion</c> (task step 3) and returns the builder.</summary>
    public static IEndpointRouteBuilder MapAdminTestCompletionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/v1/admin/models/{id:guid}/test-completion")
            .WithTags("Admin")
            .RequireAuthorization(policy => policy.RequireRole(AdminRole));

        group.MapPost(string.Empty, TestCompletionHandlers.TestCompletion)
            .WithName("AdminTestModelCompletion");

        return endpoints;
    }
}
