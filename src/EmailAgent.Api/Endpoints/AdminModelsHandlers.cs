using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using EmailAgent.Api.Errors;
using EmailAgent.Api.Models;
using EmailAgent.Api.Pagination;
using EmailAgent.Api.Sources;
using EmailAgent.Core.Identity;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace EmailAgent.Api.Endpoints;

// ---------------------------------------------------------------------------
// M3T3: the HTTP half of /v1/admin/models. Each handler
//   1. resolves the actor (401/403 parity with the admin/user namespaces),
//   2. validates the request SHAPE (§14 → 400 /errors/validation) and binds
//      the §13.13 list parameters,
//   3. calls ModelService, translating its typed failures into §13.12 RFC 7807
//      documents (including /errors/model-probe-failed with the probe's ≤ 256
//      byte response excerpt).
// Nothing else: the service owns the schema gate, the probe gate, encryption,
// persistence and the three Q-O17 audit rows, so this file has no database
// access and every outcome above stays unit-testable there. Rate-limit headers
// and the role gate come from the pipeline (§13.14 / §13.3), never from here.
// ---------------------------------------------------------------------------

/// <summary>Route handlers for <see cref="AdminModelEndpoints"/> (task step 5).</summary>
public static class AdminModelsHandlers
{
    /// <summary>§13.12 error classes this namespace emits.</summary>
    private static class ErrorTypes
    {
        public const string Validation = "/errors/validation";
        public const string Unauthorized = "/errors/unauthorized";
        public const string Forbidden = "/errors/forbidden";
        public const string ConfigValidation = "/errors/config-validation";
        public const string ModelProbeFailed = "/errors/model-probe-failed";
        public const string ModelNotFound = "/errors/model-not-found";
        public const string InternalBlobMissing = "/errors/internal-blob-missing";
    }

    /// <summary>
    /// <c>GET /v1/admin/models</c> — one keyset page (§13.13: default 25,
    /// max 100, <c>(created_at DESC, id DESC)</c>) with the
    /// <c>filter[enabled]</c> / <c>filter[provider]</c> /
    /// <c>filter[display_name_contains]</c> filters. Not audited (Q-API44 —
    /// reads are not state changes, per M1T7/M2T5 list parity).
    /// </summary>
    internal static async Task<IResult> List(
        HttpContext ctx,
        ModelService service,
        UserManager<User> userManager,
        CancellationToken ct)
    {
        var (actor, actorError) = await ResolveActorAsync(ctx, userManager, ct);
        if (actorError is not null)
        {
            return actorError;
        }

        if (!PaginationQuery.TryParseLimit(
                ctx.Request.Query["limit"].ToString(), out var limit, out var limitError))
        {
            return ValidationProblem(ctx, new[] { ("limit", limitError!) });
        }

        var rawEnabled = ctx.Request.Query["filter[enabled]"].ToString();
        bool? enabled = null;
        if (!string.IsNullOrEmpty(rawEnabled))
        {
            if (!bool.TryParse(rawEnabled, out var parsedEnabled))
            {
                return ValidationProblem(ctx, new[]
                {
                    ("filter[enabled]", "must be one of: true, false."),
                });
            }

            enabled = parsedEnabled;
        }

        var provider = NullIfEmpty(ctx.Request.Query["filter[provider]"].ToString());
        var displayNameContains = NullIfEmpty(
            ctx.Request.Query["filter[display_name_contains]"].ToString());
        var cursor = NullIfEmpty(ctx.Request.Query["cursor"].ToString());

        try
        {
            var page = await service.ListAsync(
                enabled, provider, displayNameContains, cursor, limit, ct);

            return Results.Json(
                page, AdminModelsJsonContext.Default, statusCode: StatusCodes.Status200OK);
        }
        catch (Exception exception) when (IsModelFailure(exception))
        {
            return ModelProblem(ctx, exception);
        }
    }

    /// <summary>
    /// <c>POST /v1/admin/models</c> — shape validation, then the service's
    /// schema gate → mandatory probe → one transaction (blob + row + audit).
    /// 201 with a <c>Location</c> header and a key-free <see cref="ModelDto"/>.
    /// A failed probe is a 400 <c>/errors/model-probe-failed</c> whose
    /// <c>detail</c> is the probe's response excerpt — no rows written.
    /// </summary>
    internal static async Task<IResult> Create(
        HttpContext ctx,
        ModelService service,
        UserManager<User> userManager,
        CancellationToken ct)
    {
        var (actor, actorError) = await ResolveActorAsync(ctx, userManager, ct);
        if (actorError is not null)
        {
            return actorError;
        }

        var (request, bodyError) = await ReadJsonBodyAsync(
            ctx, AdminModelsJsonContext.Default.CreateModelRequest, ct);
        if (request is null)
        {
            return bodyError ?? BodyRequired(ctx);
        }

        var validation = new CreateModelRequestValidator().Validate(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ctx, validation);
        }

        try
        {
            var model = await service.CreateAsync(request, actor!, ct);
            ctx.Response.Headers["Location"] = $"/v1/admin/models/{model.Id}";
            return Results.Json(
                model, AdminModelsJsonContext.Default, statusCode: StatusCodes.Status201Created);
        }
        catch (Exception exception) when (IsModelFailure(exception))
        {
            return ModelProblem(ctx, exception);
        }
    }

    /// <summary><c>GET /v1/admin/models/{id}</c> — 404 <c>/errors/model-not-found</c> when unknown.</summary>
    internal static async Task<IResult> Get(
        HttpContext ctx,
        Guid id,
        ModelService service,
        UserManager<User> userManager,
        CancellationToken ct)
    {
        var (actor, actorError) = await ResolveActorAsync(ctx, userManager, ct);
        if (actorError is not null)
        {
            return actorError;
        }

        try
        {
            var model = await service.GetAsync(id, ct);
            return Results.Json(
                model, AdminModelsJsonContext.Default, statusCode: StatusCodes.Status200OK);
        }
        catch (Exception exception) when (IsModelFailure(exception))
        {
            return ModelProblem(ctx, exception);
        }
    }

    /// <summary>
    /// <c>PATCH /v1/admin/models/{id}</c> — partial update; the service probes
    /// on every call and updates nothing when it fails (task scope table). An
    /// empty body is a §13.12 400 before anything else runs.
    /// </summary>
    internal static async Task<IResult> Update(
        HttpContext ctx,
        Guid id,
        ModelService service,
        UserManager<User> userManager,
        CancellationToken ct)
    {
        var (actor, actorError) = await ResolveActorAsync(ctx, userManager, ct);
        if (actorError is not null)
        {
            return actorError;
        }

        var (request, bodyError) = await ReadJsonBodyAsync(
            ctx, AdminModelsJsonContext.Default.UpdateModelRequest, ct);
        if (request is null)
        {
            return bodyError ?? BodyRequired(ctx);
        }

        var validation = new UpdateModelRequestValidator().Validate(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ctx, validation);
        }

        try
        {
            var model = await service.UpdateAsync(id, request, actor!, ct);
            return Results.Json(
                model, AdminModelsJsonContext.Default, statusCode: StatusCodes.Status200OK);
        }
        catch (Exception exception) when (IsModelFailure(exception))
        {
            return ModelProblem(ctx, exception);
        }
    }

    /// <summary>
    /// <c>DELETE /v1/admin/models/{id}</c> — catalog row + key blob + audit
    /// row in one transaction (Q-O15's pattern applied to model lifecycle).
    /// Nothing is decrypted here, so no key material is handled.
    /// </summary>
    internal static async Task<IResult> Delete(
        HttpContext ctx,
        Guid id,
        ModelService service,
        UserManager<User> userManager,
        CancellationToken ct)
    {
        var (actor, actorError) = await ResolveActorAsync(ctx, userManager, ct);
        if (actorError is not null)
        {
            return actorError;
        }

        try
        {
            await service.DeleteAsync(id, actor!, ct);
            return Results.NoContent();
        }
        catch (Exception exception) when (IsModelFailure(exception))
        {
            return ModelProblem(ctx, exception);
        }
    }

    // -----------------------------------------------------------------------
    // Actor / body plumbing (M1T7/M2T5 parity)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolves the caller through <c>UserManager</c>: no live account → 401,
    /// Disabled → 403 (a still-valid JWT must not keep admin powers for a
    /// blocked account — the AdminUserEndpoints gate).
    /// </summary>
    private static async Task<(ModelActor? Actor, IResult? Error)> ResolveActorAsync(
        HttpContext ctx, UserManager<User> userManager, CancellationToken ct)
    {
        var user = await userManager.GetUserAsync(ctx.User);
        if (user is null)
        {
            return (null, Problem(
                ctx,
                StatusCodes.Status401Unauthorized,
                ErrorTypes.Unauthorized,
                "Unauthorized",
                "The access token does not match a live account."));
        }

        if (user.Status == UserStatus.Disabled)
        {
            return (null, Problem(
                ctx,
                StatusCodes.Status403Forbidden,
                ErrorTypes.Forbidden,
                "Forbidden",
                "This account is disabled."));
        }

        return (new ModelActor(user.Id, user.Role.ToString(), ctx.Connection.RemoteIpAddress), null);
    }

    private static string? NullIfEmpty(string value)
        => string.IsNullOrEmpty(value) ? null : value;

    private static IResult BodyRequired(HttpContext ctx)
        => ValidationProblem(ctx, new[] { ("body", "A JSON request body is required.") });

    /// <summary>
    /// Reads the raw body and binds it through the pinned source-gen context
    /// (task step 7: the DTOs deserialize through <c>AdminModelsJsonContext</c>).
    /// An absent or malformed payload is a ready-made §13.12 400 so a broken
    /// body never reaches the framework binder's empty-bodied rejection.
    /// </summary>
    private static async Task<(TRequest? Body, IResult? Error)> ReadJsonBodyAsync<TRequest>(
        HttpContext ctx,
        JsonTypeInfo<TRequest> jsonTypeInfo,
        CancellationToken ct)
        where TRequest : class
    {
        string raw;
        using (var reader = new StreamReader(
            ctx.Request.Body, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true))
        {
            raw = await reader.ReadToEndAsync(ct);
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, BodyRequired(ctx));
        }

        try
        {
            var body = JsonSerializer.Deserialize(raw, jsonTypeInfo);
            return body is null
                ? (null, BodyRequired(ctx))
                : (body, null);
        }
        catch (JsonException ex)
        {
            return (null, Problem(
                ctx,
                StatusCodes.Status400BadRequest,
                ErrorTypes.Validation,
                "Validation failed",
                "The request body is not valid JSON.",
                new[] { ("body", ex.Message) }));
        }
    }

    // -----------------------------------------------------------------------
    // Problem documents (§13.12)
    // -----------------------------------------------------------------------

    /// <summary>The typed failures <see cref="ModelService"/> can raise here.</summary>
    private static bool IsModelFailure(Exception exception) => exception is
        ModelNotFoundException
        or ModelProbeFailedException
        or ConfigValidationException
        or EncryptedBlobNotFoundException
        or InvalidBlobPurposeException
        or InvalidCursorException
        or ArgumentOutOfRangeException;

    /// <summary>§13.12 document for one typed failure (untyped ones reach the global handler → 500).</summary>
    private static IResult ModelProblem(HttpContext ctx, Exception exception)
        => exception switch
        {
            ModelNotFoundException ex => Problem(
                ctx,
                StatusCodes.Status404NotFound,
                ErrorTypes.ModelNotFound,
                "Model not found",
                $"No model with id {ex.ModelId}."),

            // Task step 6: the detail IS the probe's reconstructed response
            // excerpt — already clamped to 256 UTF-8 bytes by M3T2 (SQ-M3-3)
            // and reconstructed from the event stream, so it can never echo
            // the api_key.
            ModelProbeFailedException ex => Problem(
                ctx,
                StatusCodes.Status400BadRequest,
                ErrorTypes.ModelProbeFailed,
                "Model probe failed",
                ex.ResponseExcerpt),

            ConfigValidationException ex => Problem(
                ctx,
                StatusCodes.Status400BadRequest,
                ErrorTypes.ConfigValidation,
                "Invalid model configuration",
                "The model configuration failed the engine schema validation.",
                ConfigErrorMapper.Map(ex.Schema, ex.Issues)),

            EncryptedBlobNotFoundException or InvalidBlobPurposeException => Problem(
                ctx,
                StatusCodes.Status500InternalServerError,
                ErrorTypes.InternalBlobMissing,
                "Internal blob missing",
                "An encrypted API-key blob required by this model cannot be read."),

            InvalidCursorException ex => Problem(
                ctx,
                StatusCodes.Status400BadRequest,
                ErrorTypes.Validation,
                "Validation failed",
                "The cursor is not valid.",
                new[] { ("cursor", ex.Message) }),

            ArgumentOutOfRangeException ex => Problem(
                ctx,
                StatusCodes.Status400BadRequest,
                ErrorTypes.Validation,
                "Validation failed",
                "One or more fields are invalid.",
                new[] { ("limit", ex.Message) }),

            _ => throw new UnreachableException(
                $"{nameof(ModelProblem)} must only be called for {nameof(IsModelFailure)} failures."),
        };

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
        FluentValidation.Results.ValidationResult validation)
        => ValidationProblem(
            ctx,
            validation.Errors
                .Select(error => (Name: WireFieldName(error.PropertyName), Reason: error.ErrorMessage))
                .ToList());

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

    /// <summary>
    /// CLR property name → the §13.13 snake_case member the client sent (the
    /// wire fields are snake_case: validation failures name
    /// <c>display_name</c>, not <c>DisplayName</c>). Unknown names (the
    /// <c>body</c> sentinel of the empty-PATCH rule) pass through.
    /// </summary>
    private static string WireFieldName(string propertyName)
        => propertyName switch
        {
            nameof(CreateModelRequest.DisplayName) => "display_name",
            nameof(CreateModelRequest.Provider) => "provider",
            nameof(CreateModelRequest.BaseUrl) => "base_url",
            nameof(CreateModelRequest.ModelName) => "model_name",
            nameof(CreateModelRequest.ApiKey) => "api_key",
            nameof(CreateModelRequest.PricePer1kInputUsd) => "price_per_1k_input_usd",
            nameof(CreateModelRequest.PricePer1kOutputUsd) => "price_per_1k_output_usd",
            nameof(CreateModelRequest.Enabled) => "enabled",
            _ => propertyName,
        };
}
