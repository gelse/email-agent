using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmailAgent.Api.Actions;
using EmailAgent.Api.Auth;
using EmailAgent.Api.Errors;
using EmailAgent.Api.Pagination;
using EmailAgent.Api.Sources;
using EmailAgent.Core.Security;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using InvalidCursorException = EmailAgent.Api.Pagination.InvalidCursorException;

namespace EmailAgent.Api.Endpoints;

/// <summary>M4T3: HTTP handlers for /v1/user/actions and /v1/user/notifications.</summary>
public static class ActionsHandlers
{
    // ----------------------------- Actions ---------------------------------

    internal static async Task<IResult> ListActions(
        HttpContext ctx, ActionConfigService service, UserManager<User> userManager,
        ICryptoSessionAccessor cryptoSession, CancellationToken ct)
    {
        var (actor, actorError) = await ResolveActionActorAsync(ctx, userManager, ct);
        if (actorError is not null) return actorError;

        var (dek, dekError) = ReadDek(ctx, cryptoSession);
        if (dekError is not null) return dekError;

        try
        {
            if (!PaginationQuery.TryParseLimit(ctx.Request.Query["limit"].ToString(), out var limit, out var limitError))
                return ValidationProblem(ctx, [("limit", limitError!)]);

            var cursor = ctx.Request.Query["cursor"].ToString();
            // SQ-M4-6 / spec: filter[type_id]
            var typeId = ctx.Request.Query["filter[type_id]"].ToString();
            if (string.IsNullOrEmpty(typeId)) typeId = ctx.Request.Query["type_id"].ToString();
            if (string.IsNullOrEmpty(typeId)) typeId = null;

            var page = await service.ListAsync(actor!, dek!, typeId, string.IsNullOrEmpty(cursor) ? null : cursor, limit, ct);
            var dtoPage = new PagedResult<ActionConfigDto>
            {
                Items = page.Items.Select(ActionConfigDto.FromRow).ToList(),
                NextCursor = page.NextCursor,
            };
            return Results.Json(dtoPage, ActionsJsonContext.Default, statusCode: StatusCodes.Status200OK);
        }
        catch (Exception ex) when (IsActionFailure(ex)) { return ActionProblem(ctx, ex); }
        catch (EmailAgent.Api.Pagination.InvalidCursorException ex)
        {
            return Problem(ctx, 400, ActionErrors.Types.Validation, "Validation failed", "The cursor is not valid.",
                [("cursor", ex.Message)]);
        }
        finally { Zero(dek); }
    }

    internal static async Task<IResult> CreateAction(
        HttpContext ctx, ActionConfigService service, UserManager<User> userManager,
        ICryptoSessionAccessor cryptoSession, CancellationToken ct)
    {
        var (actor, actorError) = await ResolveActionActorAsync(ctx, userManager, ct);
        if (actorError is not null) return actorError;

        var (dek, dekError) = ReadDek(ctx, cryptoSession);
        if (dekError is not null) return dekError;

        try
        {
            var (request, bodyError) = await ReadJsonBodyAsync<CreateActionRequest>(ctx, ct);
            if (request is null) return bodyError ?? BodyRequired(ctx);

            var validation = new CreateActionRequestValidator().Validate(request);
            if (!validation.IsValid) return ValidationProblem(ctx, validation);

            var row = await service.CreateAsync(
                actor!, dek!, request.TypeId!.Trim(), request.DisplayName!,
                request.RequireConfirmation, request.DryRunDefault, request.Config, ct);

            ctx.Response.Headers["Location"] = $"/v1/user/actions/{row.Entity.Id}";
            return Results.Json(ActionConfigDto.FromRow(row), ActionsJsonContext.Default, statusCode: StatusCodes.Status201Created);
        }
        catch (Exception ex) when (IsActionFailure(ex)) { return ActionProblem(ctx, ex); }
        finally { Zero(dek); }
    }

    internal static async Task<IResult> GetAction(
        HttpContext ctx, Guid id, ActionConfigService service, UserManager<User> userManager,
        ICryptoSessionAccessor cryptoSession, CancellationToken ct)
    {
        var (actor, actorError) = await ResolveActionActorAsync(ctx, userManager, ct);
        if (actorError is not null) return actorError;

        var (dek, dekError) = ReadDek(ctx, cryptoSession);
        if (dekError is not null) return dekError;

        try
        {
            var row = await service.GetAsync(actor!, dek!, id, ct);
            return Results.Json(ActionConfigDto.FromRow(row), ActionsJsonContext.Default, statusCode: StatusCodes.Status200OK);
        }
        catch (Exception ex) when (IsActionFailure(ex)) { return ActionProblem(ctx, ex); }
        finally { Zero(dek); }
    }

    internal static async Task<IResult> UpdateAction(
        HttpContext ctx, Guid id, ActionConfigService service, UserManager<User> userManager,
        ICryptoSessionAccessor cryptoSession, CancellationToken ct)
    {
        var (actor, actorError) = await ResolveActionActorAsync(ctx, userManager, ct);
        if (actorError is not null) return actorError;

        var (dek, dekError) = ReadDek(ctx, cryptoSession);
        if (dekError is not null) return dekError;

        try
        {
            var (request, bodyError) = await ReadJsonBodyAsync<UpdateActionRequest>(ctx, ct);
            if (request is null) return bodyError ?? BodyRequired(ctx);

            var validation = new UpdateActionRequestValidator().Validate(request);
            if (!validation.IsValid) return ValidationProblem(ctx, validation);

            JsonElement? config = request.Config.ValueKind == JsonValueKind.Undefined ? null : request.Config;
            var row = await service.UpdateAsync(
                actor!, dek!, id, request.DisplayName, request.RequireConfirmation, request.DryRunDefault, config, ct);
            return Results.Json(ActionConfigDto.FromRow(row), ActionsJsonContext.Default, statusCode: StatusCodes.Status200OK);
        }
        catch (Exception ex) when (IsActionFailure(ex)) { return ActionProblem(ctx, ex); }
        finally { Zero(dek); }
    }

    internal static async Task<IResult> DeleteAction(
        HttpContext ctx, Guid id, ActionConfigService service, UserManager<User> userManager, CancellationToken ct)
    {
        var (actor, actorError) = await ResolveActionActorAsync(ctx, userManager, ct);
        if (actorError is not null) return actorError;

        try
        {
            await service.DeleteAsync(actor!, id, ct);
            return Results.NoContent();
        }
        catch (Exception ex) when (IsActionFailure(ex)) { return ActionProblem(ctx, ex); }
    }

    // --------------------------- Notifications -----------------------------

    internal static async Task<IResult> ListNotifications(
        HttpContext ctx, NotificationService service, UserManager<User> userManager, CancellationToken ct)
    {
        var (actor, actorError) = await ResolveNotificationActorAsync(ctx, userManager, ct);
        if (actorError is not null) return actorError;

        try
        {
            if (!PaginationQuery.TryParseLimit(ctx.Request.Query["limit"].ToString(), out var limit, out var limitError))
                return ValidationProblem(ctx, [("limit", limitError!)]);

            var cursor = ctx.Request.Query["cursor"].ToString();
            bool? unreadOnly = null;
            var unreadRaw = ctx.Request.Query["filter[unread]"].ToString();
            if (string.IsNullOrEmpty(unreadRaw)) unreadRaw = ctx.Request.Query["unread"].ToString();
            if (!string.IsNullOrEmpty(unreadRaw))
            {
                if (bool.TryParse(unreadRaw, out var b)) unreadOnly = b;
                else if (unreadRaw == "1") unreadOnly = true;
                else if (unreadRaw == "0") unreadOnly = false;
                else return ValidationProblem(ctx, [("filter[unread]", "must be a boolean.")]);
            }

            var page = await service.ListAsync(actor!, unreadOnly, string.IsNullOrEmpty(cursor) ? null : cursor, limit, ct);
            var dtoPage = new PagedResult<NotificationDto>
            {
                Items = page.Items.Select(NotificationDto.FromEntity).ToList(),
                NextCursor = page.NextCursor,
            };
            return Results.Json(dtoPage, NotificationsJsonContext.Default, statusCode: StatusCodes.Status200OK);
        }
        catch (EmailAgent.Api.Pagination.InvalidCursorException ex)
        {
            return Problem(ctx, 400, ActionErrors.Types.Validation, "Validation failed", "The cursor is not valid.",
                [("cursor", ex.Message)]);
        }
    }

    internal static async Task<IResult> MarkRead(
        HttpContext ctx, Guid id, NotificationService service, UserManager<User> userManager, CancellationToken ct)
    {
        var (actor, actorError) = await ResolveNotificationActorAsync(ctx, userManager, ct);
        if (actorError is not null) return actorError;

        try
        {
            var row = await service.MarkReadAsync(actor!, id, ct);
            return Results.Json(NotificationDto.FromEntity(row), NotificationsJsonContext.Default, statusCode: StatusCodes.Status200OK);
        }
        catch (NotificationNotFoundException ex)
        {
            return Problem(ctx, 404, ActionErrors.Types.NotificationNotFound, "Notification not found",
                $"No notification with id {ex.NotificationId}.");
        }
    }

    internal static async Task<IResult> MarkAllRead(
        HttpContext ctx, NotificationService service, UserManager<User> userManager, CancellationToken ct)
    {
        var (actor, actorError) = await ResolveNotificationActorAsync(ctx, userManager, ct);
        if (actorError is not null) return actorError;

        var updated = await service.MarkAllReadAsync(actor!, ct);
        return Results.Json(new ReadAllResponse { Updated = updated }, NotificationsJsonContext.Default,
            statusCode: StatusCodes.Status200OK);
    }

    // ----------------------------- Plumbing --------------------------------

    private static async Task<(ActionActor? Actor, IResult? Error)> ResolveActionActorAsync(
        HttpContext ctx, UserManager<User> userManager, CancellationToken ct)
    {
        var (user, err) = await ResolveUserAsync(ctx, userManager);
        return user is null ? (null, err) : (new ActionActor(user.Id, user.Role.ToString(), ctx.Connection.RemoteIpAddress), null);
    }

    private static async Task<(NotificationActor? Actor, IResult? Error)> ResolveNotificationActorAsync(
        HttpContext ctx, UserManager<User> userManager, CancellationToken ct)
    {
        var (user, err) = await ResolveUserAsync(ctx, userManager);
        return user is null ? (null, err) : (new NotificationActor(user.Id, user.Role.ToString(), ctx.Connection.RemoteIpAddress), null);
    }

    private static async Task<(User? User, IResult? Error)> ResolveUserAsync(HttpContext ctx, UserManager<User> userManager)
    {
        var user = await userManager.GetUserAsync(ctx.User);
        if (user is null)
        {
            return (null, Problem(ctx, 401, ActionErrors.Types.Unauthorized, "Unauthorized",
                "The access token does not match a live account."));
        }
        if (user.Status == UserStatus.Disabled)
        {
            return (null, Problem(ctx, 403, ActionErrors.Types.Forbidden, "Forbidden", "This account is disabled."));
        }
        return (user, null);
    }

    /// <summary>
    /// M5T1: resolves the DEK from the crypto-session accessor (the M2–M4
    /// <c>X-User-Dek</c> header and its <c>/errors/dek-required</c> /
    /// <c>/errors/dek-invalid</c> verdicts are removed — superseded by the
    /// generic 403 <c>/errors/crypto-locked</c> the namespace guard emits
    /// before any handler runs; this fallback covers direct invocation only).
    /// </summary>
    private static (byte[]? Dek, IResult? Error) ReadDek(HttpContext ctx, ICryptoSessionAccessor cryptoSession)
    {
        var result = cryptoSession.TryGetDek();
        if (!result.IsOk || result.Dek is null)
        {
            return (null, CryptoLockedProblem(ctx));
        }

        return (result.Dek, null);
    }

    /// <summary>403 <c>/errors/crypto-locked</c> — unlock again with the passphrase (SQ-M5-1).</summary>
    private static IResult CryptoLockedProblem(HttpContext ctx)
        => Problem(ctx, 403, CryptoSessionMiddleware.CryptoLockedErrorType, "Crypto session locked",
            "The data-encryption vault for this session is locked or expired. "
            + "Unlock with the account passphrase and retry.");

    private static readonly JsonSerializerOptions BodySerializerOptions = new(JsonSerializerDefaults.Web);

    private static async Task<(TRequest? Body, IResult? Error)> ReadJsonBodyAsync<TRequest>(HttpContext ctx, CancellationToken ct)
        where TRequest : class
    {
        string raw;
        using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, false, 4096, true))
            raw = await reader.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(raw)) return (null, BodyRequired(ctx));
        try
        {
            var body = JsonSerializer.Deserialize<TRequest>(raw, BodySerializerOptions);
            return body is null ? (null, BodyRequired(ctx)) : (body, null);
        }
        catch (JsonException ex)
        {
            return (null, Problem(ctx, 400, ActionErrors.Types.Validation, "Validation failed",
                "The request body is not valid JSON.", [("body", ex.Message)]));
        }
    }

    private static IResult BodyRequired(HttpContext ctx)
        => Problem(ctx, 400, ActionErrors.Types.Validation, "Validation failed", "A JSON request body is required.",
            [("body", "A JSON request body is required.")]);

    private static bool IsActionFailure(Exception ex) => ex is
        ActionConfigNotFoundException or ActionPluginNotRegisteredException
        or ActionConfigValidationException
        or ActionDekInvalidException or ActionBlobMissingException
        or ActionInUseException or InvalidCursorException;

    private static IResult ActionProblem(HttpContext ctx, Exception ex) => ex switch
    {
        ActionConfigNotFoundException e => Problem(ctx, 404, ActionErrors.Types.ActionNotFound, "Action not found",
            $"No action config with id {e.ActionId}."),
        ActionPluginNotRegisteredException e => Problem(ctx, 400, ActionErrors.Types.PluginNotRegistered, "Plugin not registered", e.Message),
        ActionConfigValidationException e => Problem(ctx, 400, ActionErrors.Types.ConfigValidation, "Invalid action configuration",
            "The action configuration failed the handler's schema validation.", ConfigErrorMapper.Map(e.Schema, e.Issues)),
        // M5T1: a blob that the session DEK cannot open means the session key
        // is wrong for this account's data — the re-unlock verdict (403
        // /errors/crypto-locked), not M4T3's 400 /errors/dek-invalid.
        ActionDekInvalidException => Problem(ctx, 403, CryptoSessionMiddleware.CryptoLockedErrorType,
            "Crypto session locked",
            "The session's data-encryption key does not open this action's configuration. "
            + "Unlock with the account passphrase and retry."),
        ActionBlobMissingException => Problem(ctx, 500, ActionErrors.Types.InternalBlobMissing, "Internal blob missing",
            "An encrypted configuration blob required by this action cannot be read."),
        ActionInUseException e => Problem(ctx, 409, ActionErrors.Types.ActionInUse, "Action in use",
            $"Action config {e.ActionId} is referenced by pending actions and cannot be deleted."),
        InvalidCursorException e => Problem(ctx, 400, ActionErrors.Types.Validation, "Validation failed",
            "The cursor is not valid.", [("cursor", e.Message)]),
        _ => throw new UnreachableException(),
    };

    private static IResult Problem(HttpContext ctx, int status, string type, string title, string? detail = null,
        IReadOnlyList<(string Name, string Reason)>? errors = null)
        => ProblemDetailsHelpers.Problem(ctx, status, type, title, detail, errors);

    private static IResult ValidationProblem(HttpContext ctx, FluentValidation.Results.ValidationResult validation)
        => ValidationProblem(ctx, validation.Errors.Select(e => (Name: WireFieldName(e.PropertyName), Reason: e.ErrorMessage)).ToList());

    private static IResult ValidationProblem(HttpContext ctx, IReadOnlyList<(string Name, string Reason)> errors)
        => Problem(ctx, 400, ActionErrors.Types.Validation, "Validation failed", "One or more fields are invalid.", errors);

    private static string WireFieldName(string propertyName) => propertyName switch
    {
        nameof(CreateActionRequest.TypeId) => "type_id",
        nameof(CreateActionRequest.DisplayName) => "display_name",
        nameof(CreateActionRequest.RequireConfirmation) => "require_confirmation",
        nameof(CreateActionRequest.DryRunDefault) => "dry_run_default",
        nameof(CreateActionRequest.Config) => "config",
        _ => propertyName,
    };

    private static void Zero(byte[]? dek)
    {
        if (dek is not null) CryptographicOperations.ZeroMemory(dek);
    }
}
