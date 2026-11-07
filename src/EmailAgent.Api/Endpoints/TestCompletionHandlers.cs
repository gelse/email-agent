using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EmailAgent.Api.Errors;
using EmailAgent.Api.Models;
using EmailAgent.Api.Sources;
using EmailAgent.Core.Identity;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace EmailAgent.Api.Endpoints;

// ---------------------------------------------------------------------------
// M3T6: the HTTP half of POST /v1/admin/models/{id}/test-completion. Each
// stage mirrors M3T3's handler shape: resolve the actor (401/403 parity),
// bind + validate the body (§13.12 document with an errors[] array), call
// TestCompletionService, translate its typed failures into RFC 7807 — no
// database access and no engine code here. Rate-limit headers and the role
// gate come from the pipeline (§13.14 / §13.3), never from this file.
// ---------------------------------------------------------------------------

/// <summary>Route handlers for <see cref="AdminTestCompletionEndpoints"/> (task step 4).</summary>
public static class TestCompletionHandlers
{
    /// <summary>§13.12 error classes this endpoint emits.</summary>
    private static class ErrorTypes
    {
        public const string Unauthorized = "/errors/unauthorized";
        public const string Forbidden = "/errors/forbidden";
        public const string ConfigValidation = "/errors/config-validation";
        public const string ModelDisabled = "/errors/model-disabled";
        public const string ModelNotFound = "/errors/model-not-found";
        public const string TestCompletionFailed = "/errors/test-completion-failed";
        public const string InternalBlobMissing = "/errors/internal-blob-missing";
    }

    /// <summary>
    /// <c>POST /v1/admin/models/{id}/test-completion</c> — one manual,
    /// non-streaming completion against the persisted model (task scope
    /// table). Input-shape failures (absent/malformed body, empty
    /// <c>user_prompt</c>, bad <c>tools[]</c>) are all 400
    /// <c>/errors/config-validation</c> documents with an <c>errors[]</c>
    /// array naming the wire members — the single 400-input class the task's
    /// error table defines for this endpoint (M3T3's catalog CRUD keeps its
    /// own <c>/errors/validation</c> class; the two namespaces are
    /// deliberately independent). A request rejected here runs NO engine
    /// call and writes NO audit row: <c>test_model_completion</c> records
    /// COMPLETED calls only (SQ-M3-4's field set — duration, tokens, cost —
    /// exists only once a completion returned).
    /// <para>
    /// Q-L8's per-user soft token cap is intentionally NOT enforced (task
    /// Out): this is an admin-driven manual exerciser, not a per-message
    /// generation call, so no user budget is charged for its tokens.
    /// </para>
    /// </summary>
    internal static async Task<IResult> TestCompletion(
        HttpContext ctx,
        Guid id,
        TestCompletionService service,
        UserManager<User> userManager,
        CancellationToken ct)
    {
        var (actor, actorError) = await ResolveActorAsync(ctx, userManager, ct);
        if (actorError is not null)
        {
            return actorError;
        }

        var (request, bodyError) = await ReadJsonBodyAsync(ctx, ct);
        if (request is null)
        {
            return bodyError!;
        }

        var validation = new TestCompletionRequestValidator().Validate(request);
        if (!validation.IsValid)
        {
            return ConfigValidationProblem(ctx, validation);
        }

        try
        {
            var response = await service.ExecuteAsync(id, request, actor!, ct);
            return Results.Json(
                response,
                TestCompletionJsonContext.Default.TestCompletionResponse,
                statusCode: StatusCodes.Status200OK);
        }
        catch (Exception exception) when (IsTestCompletionFailure(exception))
        {
            return TestCompletionProblem(ctx, exception);
        }
    }

    // -----------------------------------------------------------------------
    // Actor / body plumbing (M1T7/M2T5/M3T3 parity)
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

    /// <summary>
    /// Reads the raw body and binds it through the pinned source-gen context
    /// (task step 6). An absent or malformed payload is a ready-made §13.12
    /// 400 <c>/errors/config-validation</c> so a broken body never reaches the
    /// framework binder's empty-bodied rejection.
    /// </summary>
    private static async Task<(TestCompletionRequest? Body, IResult? Error)> ReadJsonBodyAsync(
        HttpContext ctx, CancellationToken ct)
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
            return (null, ConfigValidationProblem(
                ctx, new[] { ("body", "A JSON request body is required.") }));
        }

        try
        {
            var body = JsonSerializer.Deserialize(
                raw, TestCompletionJsonContext.Default.TestCompletionRequest);
            return body is null
                ? (null, ConfigValidationProblem(
                    ctx, new[] { ("body", "A JSON request body is required.") }))
                : (body, null);
        }
        catch (JsonException ex)
        {
            return (null, ConfigValidationProblem(
                ctx, new[] { ("body", "The request body is not valid JSON: " + ex.Message) }));
        }
    }

    // -----------------------------------------------------------------------
    // Problem documents (§13.12)
    // -----------------------------------------------------------------------

    /// <summary>The typed failures <see cref="TestCompletionService"/> can raise here.</summary>
    private static bool IsTestCompletionFailure(Exception exception) => exception is
        ModelNotFoundException
        or ModelDisabledException
        or ConfigValidationException
        or TestCompletionFailedException
        or EncryptedBlobNotFoundException
        or InvalidBlobPurposeException;

    /// <summary>§13.12 document for one typed failure (untyped ones reach the global handler → 500).</summary>
    private static IResult TestCompletionProblem(HttpContext ctx, Exception exception)
        => exception switch
        {
            ModelNotFoundException ex => Problem(
                ctx,
                StatusCodes.Status404NotFound,
                ErrorTypes.ModelNotFound,
                "Model not found",
                $"No model with id {ex.ModelId}."),

            ModelDisabledException => Problem(
                ctx,
                StatusCodes.Status400BadRequest,
                ErrorTypes.ModelDisabled,
                "Model disabled",
                "The model is disabled — a test completion only runs against enabled models."),

            // The service raises this when the row's api_key_secret_ref is
            // unreadable; the mapped errors[] blames api_key exactly as M3T3's
            // PATCH path does.
            ConfigValidationException ex => Problem(
                ctx,
                StatusCodes.Status400BadRequest,
                ErrorTypes.ConfigValidation,
                "Invalid model configuration",
                ex.Message,
                ConfigErrorMapper.Map(ex.Schema, ex.Issues)),

            // ex.Detail is the service's ALREADY-SANITIZED detail (SQ-M3-15:
            // key shapes redacted, clamped to 256 UTF-8 bytes) — surfacing it
            // verbatim can never re-introduce the raw provider body.
            TestCompletionFailedException ex => Problem(
                ctx,
                StatusCodes.Status500InternalServerError,
                ErrorTypes.TestCompletionFailed,
                "Test completion failed",
                ex.Detail),

            EncryptedBlobNotFoundException or InvalidBlobPurposeException => Problem(
                ctx,
                StatusCodes.Status500InternalServerError,
                ErrorTypes.InternalBlobMissing,
                "Internal blob missing",
                "An encrypted API-key blob required by this model cannot be read."),

            _ => throw new UnreachableException(
                $"{nameof(TestCompletionProblem)} must only be called for "
                + $"{nameof(IsTestCompletionFailure)} failures."),
        };

    private static IResult Problem(
        HttpContext ctx,
        int status,
        string type,
        string title,
        string? detail = null,
        IReadOnlyList<(string Name, string Reason)>? errors = null)
        => ProblemDetailsHelpers.Problem(ctx, status, type, title, detail, errors);

    /// <summary>The task's 400 input class: <c>/errors/config-validation</c> with wire-named errors[].</summary>
    private static IResult ConfigValidationProblem(
        HttpContext ctx,
        IReadOnlyList<(string Name, string Reason)> errors)
        => Problem(
            ctx,
            StatusCodes.Status400BadRequest,
            ErrorTypes.ConfigValidation,
            "Invalid test-completion request",
            "One or more fields are invalid.",
            errors);

    private static IResult ConfigValidationProblem(
        HttpContext ctx,
        FluentValidation.Results.ValidationResult validation)
        => ConfigValidationProblem(
            ctx,
            validation.Errors
                .Select(error => (Name: WireFieldName(error.PropertyName), Reason: error.ErrorMessage))
                .ToList());

    /// <summary>
    /// CLR property name → the snake_case wire member the client sent.
    /// The validator overrides child members to their lowercase segments, so
    /// only the head segment needs mapping:
    /// <c>UserPrompt</c> → <c>user_prompt</c>,
    /// <c>Tools[0].name</c> → <c>tools[0].name</c>. Unknown names pass
    /// through (the <c>body</c> sentinel of the empty-body rule).
    /// </summary>
    private static string WireFieldName(string propertyName)
    {
        var bracket = propertyName.IndexOf('[');
        var head = bracket < 0 ? propertyName : propertyName[..bracket];
        var tail = bracket < 0 ? string.Empty : propertyName[bracket..];

        return head switch
        {
            nameof(TestCompletionRequest.SystemPrompt) => "system_prompt" + tail,
            nameof(TestCompletionRequest.UserPrompt) => "user_prompt" + tail,
            nameof(TestCompletionRequest.Tools) => "tools" + tail,
            _ => propertyName,
        };
    }
}
