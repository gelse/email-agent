using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace EmailAgent.Api.Endpoints;

/// <summary>
/// M4T3 / §13.3: <c>/v1/user/actions</c> CRUD + <c>/v1/user/notifications</c>.
/// Whole group <c>[Authorize(Roles = "User")]</c>.
/// </summary>
public static class ActionsEndpoints
{
    private const string UserRole = "User";

    public static IEndpointRouteBuilder MapActionsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1/user/actions")
            .WithTags("actions")
            .RequireAuthorization(policy => policy.RequireRole(UserRole));

        group.MapGet(string.Empty, ActionsHandlers.ListActions);
        group.MapPost(string.Empty, ActionsHandlers.CreateAction);
        group.MapGet("/{id:guid}", ActionsHandlers.GetAction);
        group.MapPatch("/{id:guid}", ActionsHandlers.UpdateAction);
        group.MapDelete("/{id:guid}", ActionsHandlers.DeleteAction);

        return endpoints;
    }

    public static IEndpointRouteBuilder MapNotificationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1/user/notifications")
            .WithTags("notifications")
            .RequireAuthorization(policy => policy.RequireRole(UserRole));

        group.MapGet(string.Empty, ActionsHandlers.ListNotifications);
        group.MapPost("/{id:guid}/read", ActionsHandlers.MarkRead);
        group.MapPost("/read-all", ActionsHandlers.MarkAllRead);

        return endpoints;
    }
}
