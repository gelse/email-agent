using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmailAgent.Api.Errors;
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
// M2T5: the HTTP half of /v1/user/sources. Each handler
//   1. resolves the actor (401/403 parity with the admin/account namespaces),
//   2. reads the M2-only X-User-Dek header where the operation must decrypt,
//   3. validates the request SHAPE (§14 → 400 /errors/validation), and
//   4. calls SourceService, translating its typed failures (SourceExceptions.cs)
//      into §13.12 RFC 7807 documents.
// Nothing else: the service owns ownership scoping, schema validation,
// encryption, persistence and the five §15 audit rows, so this file has no
// database access at all and every outcome above stays unit-testable there.
// ---------------------------------------------------------------------------

/// <summary>Route handlers for <see cref="SourcesEndpoints"/> (task step 6/7).</summary>
public static class SourcesHandlers
{
    /// <summary>
    /// M2-only DEK transport (task Risks SQ-M2-1 / §13.16): the SPA unwraps
    /// the per-user data-encryption key and sends it per request. M5 replaces
    /// the header with the session-key envelope and deletes this constant's
    /// only call sites.
    /// </summary>
    public const string DekHeaderName = "X-User-Dek";

    /// <summary>The 32-byte AES-256 key length every DEK header must decode to.</summary>
    private const int DekByteLength = 32;

    /// <summary>
    /// <c>GET /v1/user/sources</c> — one keyset page of the caller's sources
    /// with the §13.13 <c>limit</c>/<c>cursor</c> parameters and the
    /// <c>type_id</c>/<c>status</c> filters. Not audited (a read is not a
    /// state change; the five §15 source actions are the mutations).
    /// </summary>
    internal static async Task<IResult> List(
        HttpContext ctx,
        SourceService service,
        UserManager<User> userManager,
        CancellationToken ct)
    {
        var (actor, actorError) = await ResolveActorAsync(ctx, userManager, ct);
        if (actorError is not null)
        {
            return actorError;
        }

        var (dek, dekError) = ReadDek(ctx);
        if (dekError is not null)
        {
            return dekError;
        }

        try
        {
            if (!PaginationQuery.TryParseLimit(
                    ctx.Request.Query["limit"].ToString(), out var limit, out var limitError))
            {
                return ValidationProblem(ctx, new[] { ("limit", limitError!) });
            }

            var cursor = ctx.Request.Query["cursor"].ToString();
            var typeId = ctx.Request.Query["type_id"].ToString();
            var rawStatus = ctx.Request.Query["status"].ToString();

            MessageSourceStatus? status = null;
            if (!string.IsNullOrEmpty(rawStatus))
            {
                if (!Enum.TryParse(rawStatus, ignoreCase: true, out MessageSourceStatus parsed)
                    || !Enum.IsDefined(parsed)
                    || !string.Equals(
                        parsed.ToString(), rawStatus, StringComparison.OrdinalIgnoreCase))
                {
                    return ValidationProblem(ctx, new[]
                    {
                        ("status", "must be one of: Active, Paused, Error."),
                    });
                }

                status = parsed;
            }

            var page = await service.ListAsync(
                actor!,
                dek!,
                string.IsNullOrEmpty(typeId) ? null : typeId,
                status,
                string.IsNullOrEmpty(cursor) ? null : cursor,
                limit,
                ct);

            return Results.Json(page, SourcesJsonContext.Default, statusCode: StatusCodes.Status200OK);
        }
        catch (Exception exception) when (IsSourceFailure(exception))
        {
            return SourceProblem(ctx, exception);
        }
        finally
        {
            Zero(dek);
        }
    }

    /// <summary>
    /// <c>POST /v1/user/sources</c> — plugin + schema gate, encrypted config
    /// blob, source row and <c>create_source</c> audit row in one transaction
    /// (SQ-7). 201 with a <c>Location</c> header pointing at the new resource.
    /// </summary>
    internal static async Task<IResult> Create(
        HttpContext ctx,
        SourceService service,
        UserManager<User> userManager,
        CancellationToken ct)
    {
        var (actor, actorError) = await ResolveActorAsync(ctx, userManager, ct);
        if (actorError is not null)
        {
            return actorError;
        }

        var (dek, dekError) = ReadDek(ctx);
        if (dekError is not null)
        {
            return dekError;
        }

        try
        {
            var (request, bodyError) = await ReadJsonBodyAsync<CreateSourceRequest>(ctx, ct);
            if (request is null)
            {
                return bodyError ?? BodyRequired(ctx);
            }

            var validation = new CreateSourceRequestValidator().Validate(request);
            if (!validation.IsValid)
            {
                return ValidationProblem(ctx, validation);
            }

            var source = await service.CreateAsync(
                actor!,
                dek!,
                request.TypeId!.Trim(),
                request.DisplayName!,
                request.IntervalMinutes,
                request.Config,
                ct);

            ctx.Response.Headers["Location"] = $"/v1/user/sources/{source.Id}";
            return Results.Json(
                source, SourcesJsonContext.Default, statusCode: StatusCodes.Status201Created);
        }
        catch (Exception exception) when (IsSourceFailure(exception))
        {
            return SourceProblem(ctx, exception);
        }
        finally
        {
            Zero(dek);
        }
    }

    /// <summary>
    /// <c>GET /v1/user/sources/{id}</c> — decrypted projection with the
    /// password stripped; a foreign id is a 404, never a 403 (no existence
    /// oracle, task Risks).
    /// </summary>
    internal static async Task<IResult> Get(
        HttpContext ctx,
        Guid id,
        SourceService service,
        UserManager<User> userManager,
        CancellationToken ct)
    {
        var (actor, actorError) = await ResolveActorAsync(ctx, userManager, ct);
        if (actorError is not null)
        {
            return actorError;
        }

        var (dek, dekError) = ReadDek(ctx);
        if (dekError is not null)
        {
            return dekError;
        }

        try
        {
            var source = await service.GetAsync(actor!, dek!, id, ct);
            return Results.Json(source, SourcesJsonContext.Default, statusCode: StatusCodes.Status200OK);
        }
        catch (Exception exception) when (IsSourceFailure(exception))
        {
            return SourceProblem(ctx, exception);
        }
        finally
        {
            Zero(dek);
        }
    }

    /// <summary>
    /// <c>PATCH /v1/user/sources/{id}</c> — optional <c>display_name</c>,
    /// <c>interval_minutes</c> and <c>config</c>; an absent or empty password
    /// inside a supplied config keeps the stored one (task Risks).
    /// </summary>
    internal static async Task<IResult> Update(
        HttpContext ctx,
        Guid id,
        SourceService service,
        UserManager<User> userManager,
        CancellationToken ct)
    {
        var (actor, actorError) = await ResolveActorAsync(ctx, userManager, ct);
        if (actorError is not null)
        {
            return actorError;
        }

        var (dek, dekError) = ReadDek(ctx);
        if (dekError is not null)
        {
            return dekError;
        }

        try
        {
            var (request, bodyError) = await ReadJsonBodyAsync<UpdateSourceRequest>(ctx, ct);
            if (request is null)
            {
                return bodyError ?? BodyRequired(ctx);
            }

            var validation = new UpdateSourceRequestValidator().Validate(request);
            if (!validation.IsValid)
            {
                return ValidationProblem(ctx, validation);
            }

            // Undefined means the member was absent — the service keeps the
            // stored document in that case (task Risks "leave unchanged").
            JsonElement? config = request.Config.ValueKind == JsonValueKind.Undefined
                ? null
                : request.Config;

            var source = await service.UpdateAsync(
                actor!, dek!, id, request.DisplayName, request.IntervalMinutes, config, ct);

            return Results.Json(source, SourcesJsonContext.Default, statusCode: StatusCodes.Status200OK);
        }
        catch (Exception exception) when (IsSourceFailure(exception))
        {
            return SourceProblem(ctx, exception);
        }
        finally
        {
            Zero(dek);
        }
    }

    /// <summary>
    /// <c>DELETE /v1/user/sources/{id}</c> — source row, config blob and
    /// <c>delete_source</c> audit row in one transaction (SQ-7). No DEK header:
    /// nothing is decrypted, so no key material is handled. A source that still
    /// owns <c>processed_messages</c> rows is a 409 (Q-O15 keeps them and the
    /// FK is RESTRICT — see <see cref="SourceHasMessagesException"/>).
    /// </summary>
    internal static async Task<IResult> Delete(
        HttpContext ctx,
        Guid id,
        SourceService service,
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
            await service.DeleteAsync(actor!, id, ct);
            return Results.NoContent();
        }
        catch (Exception exception) when (IsSourceFailure(exception))
        {
            return SourceProblem(ctx, exception);
        }
    }

    /// <summary>
    /// <c>POST /v1/user/sources/test-connection</c> — always HTTP 200 with
    /// <c>{ ok, error }</c>: a refused login or unreachable host is a plugin
    /// outcome the SPA renders inline (milestone row), while a failure the API
    /// itself can prove (unknown plugin, schema violation such as
    /// <c>port=99999</c>) is a §13.12 400 raised BEFORE any socket is opened
    /// (task Risks). No DEK header: nothing is stored or read.
    /// </summary>
    internal static async Task<IResult> TestConnection(
        HttpContext ctx,
        SourceService service,
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
            var (request, bodyError) = await ReadJsonBodyAsync<TestConnectionRequest>(ctx, ct);
            if (request is null)
            {
                return bodyError ?? BodyRequired(ctx);
            }

            var validation = new TestConnectionRequestValidator().Validate(request);
            if (!validation.IsValid)
            {
                return ValidationProblem(ctx, validation);
            }

            var outcome = await service.TestConnectionAsync(
                actor!, request.TypeId!.Trim(), request.Config, ct);

            return Results.Json(
                new TestConnectionResponse { Ok = outcome.Ok, Error = outcome.Error },
                SourcesJsonContext.Default,
                statusCode: StatusCodes.Status200OK);
        }
        catch (Exception exception) when (IsSourceFailure(exception))
        {
            return SourceProblem(ctx, exception);
        }
    }

    /// <summary>
    /// <c>POST /v1/user/sources/{id}/fetch-now</c> — the M2 manual exerciser:
    /// iterate the connector's new messages, persist each with
    /// <c>INSERT … ON CONFLICT DO NOTHING</c> plus its encrypted body blob,
    /// advance the checkpoint and audit. No engine is reachable from here
    /// (<c>M2_NoLlmCall_Happens</c>).
    /// </summary>
    internal static async Task<IResult> FetchNow(
        HttpContext ctx,
        Guid id,
        SourceService service,
        UserManager<User> userManager,
        CancellationToken ct)
    {
        var (actor, actorError) = await ResolveActorAsync(ctx, userManager, ct);
        if (actorError is not null)
        {
            return actorError;
        }

        var (dek, dekError) = ReadDek(ctx);
        if (dekError is not null)
        {
            return dekError;
        }

        try
        {
            var outcome = await service.FetchNowAsync(actor!, dek!, id, ct);

            return Results.Json(
                new FetchNowResponse
                {
                    Processed = outcome.Processed,
                    NewProcessedMessages = outcome.NewProcessedMessages,
                    Checkpoint = new SourceCheckpointDto
                    {
                        LastUid = outcome.Checkpoint.LastUid,
                        Extra = outcome.Checkpoint.Extra,
                    },
                },
                SourcesJsonContext.Default,
                statusCode: StatusCodes.Status200OK);
        }
        catch (Exception exception) when (IsSourceFailure(exception))
        {
            return SourceProblem(ctx, exception);
        }
        finally
        {
            Zero(dek);
        }
    }

    // -----------------------------------------------------------------------
    // Actor / header / body plumbing
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolves the caller through <c>UserManager</c>: no live account → 401,
    /// Disabled → 403 (identical gates to the account/admin namespaces, so a
    /// still-valid JWT cannot act on a blocked account).
    /// </summary>
    private static async Task<(SourceActor? Actor, IResult? Error)> ResolveActorAsync(
        HttpContext ctx, UserManager<User> userManager, CancellationToken ct)
    {
        var user = await userManager.GetUserAsync(ctx.User);
        if (user is null)
        {
            return (null, Problem(
                ctx,
                StatusCodes.Status401Unauthorized,
                SourceErrors.Types.Unauthorized,
                "Unauthorized",
                "The access token does not match a live account."));
        }

        if (user.Status == UserStatus.Disabled)
        {
            return (null, Problem(
                ctx,
                StatusCodes.Status403Forbidden,
                SourceErrors.Types.Forbidden,
                "Forbidden",
                "This account is disabled."));
        }

        return (new SourceActor(user.Id, user.Role.ToString(), ctx.Connection.RemoteIpAddress), null);
    }

    /// <summary>
    /// Reads and decodes <c>X-User-Dek</c>. A missing, non-base64 or
    /// wrong-length value is a §13.12 400 whose <c>errors[0].name</c> is
    /// <c>x-user-dek</c> — never a 500 from an AES-GCM length check.
    /// </summary>
    private static (byte[]? Dek, IResult? Error) ReadDek(HttpContext ctx)
    {
        var header = ctx.Request.Headers[DekHeaderName].ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            return (null, DekProblem(
                ctx,
                $"the {DekHeaderName} header is required (M2-only transport; M5 replaces it "
                + "with the session-key envelope)."));
        }

        byte[] dek;
        try
        {
            dek = Convert.FromBase64String(header.Trim());
        }
        catch (FormatException)
        {
            return (null, DekProblem(ctx, $"the {DekHeaderName} header must be base64."));
        }

        if (dek.Length != DekByteLength)
        {
            Zero(dek);
            return (null, DekProblem(
                ctx,
                $"the {DekHeaderName} header must decode to {DekByteLength} bytes."));
        }

        return (dek, null);
    }

    private static IResult DekProblem(HttpContext ctx, string reason)
        => Problem(
            ctx,
            StatusCodes.Status400BadRequest,
            SourceErrors.Types.Validation,
            "Validation failed",
            "A request header is invalid.",
            new[] { ("x-user-dek", reason) });

    private static IResult BodyRequired(HttpContext ctx)
        => Problem(
            ctx,
            StatusCodes.Status400BadRequest,
            SourceErrors.Types.Validation,
            "Validation failed",
            "A JSON request body is required.",
            new[] { ("body", "A JSON request body is required.") });

    private static readonly JsonSerializerOptions BodySerializerOptions =
        new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads the raw body and binds it as <typeparamref name="TRequest"/>. An
    /// absent or malformed payload comes back as a ready-made §13.12 400
    /// (<see cref="Error"/>) so a broken JSON body never reaches the framework
    /// binder's empty-bodied rejection (M1T6 pattern, task step 7).
    /// </summary>
    private static async Task<(TRequest? Body, IResult? Error)> ReadJsonBodyAsync<TRequest>(
        HttpContext ctx,
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
            var body = JsonSerializer.Deserialize<TRequest>(raw, BodySerializerOptions);
            return body is null
                ? (null, BodyRequired(ctx))
                : (body, null);
        }
        catch (JsonException ex)
        {
            return (null, Problem(
                ctx,
                StatusCodes.Status400BadRequest,
                SourceErrors.Types.Validation,
                "Validation failed",
                "The request body is not valid JSON.",
                new[] { ("body", ex.Message) }));
        }
    }

    // -----------------------------------------------------------------------
    // Problem documents
    // -----------------------------------------------------------------------

    /// <summary>The typed failures <see cref="SourceService"/> can raise here.</summary>
    private static bool IsSourceFailure(Exception exception) => exception is
        SourceNotFoundException
        or PluginNotRegisteredException
        or ConfigValidationException
        or DekInvalidException
        or SourceHasMessagesException
        or SourceConnectionFailedException
        or InternalBlobMissingException
        or InvalidCursorException;

    /// <summary>§13.12 document for one typed failure (untyped ones reach the global handler → 500).</summary>
    private static IResult SourceProblem(HttpContext ctx, Exception exception)
        => exception switch
        {
            SourceNotFoundException ex => Problem(
                ctx,
                StatusCodes.Status404NotFound,
                SourceErrors.Types.SourceNotFound,
                "Source not found",
                $"No message source with id {ex.SourceId}."),

            PluginNotRegisteredException ex => Problem(
                ctx,
                StatusCodes.Status400BadRequest,
                SourceErrors.Types.PluginNotRegistered,
                "Plugin not registered",
                ex.Message),

            ConfigValidationException ex => Problem(
                ctx,
                StatusCodes.Status400BadRequest,
                SourceErrors.Types.ConfigValidation,
                "Invalid source configuration",
                "The source configuration failed the plugin's schema validation.",
                ConfigErrorMapper.Map(ex.Schema, ex.Issues)),

            DekInvalidException => Problem(
                ctx,
                StatusCodes.Status400BadRequest,
                SourceErrors.Types.DekInvalid,
                "Invalid data-encryption key",
                "The supplied data-encryption key does not open this source's configuration. "
                + "Send the key belonging to the signed-in account."),

            SourceHasMessagesException ex => Problem(
                ctx,
                StatusCodes.Status409Conflict,
                SourceErrors.Types.Conflict,
                "Conflict",
                ex.Message),

            SourceConnectionFailedException ex => Problem(
                ctx,
                StatusCodes.Status500InternalServerError,
                SourceErrors.Types.SourceConnectionFailed,
                "Source connection failed",
                $"Testing the connection for '{ex.TypeId}' failed unexpectedly."),

            InternalBlobMissingException => Problem(
                ctx,
                StatusCodes.Status500InternalServerError,
                SourceErrors.Types.InternalBlobMissing,
                "Internal blob missing",
                "An encrypted configuration blob required by this source cannot be read."),

            InvalidCursorException ex => Problem(
                ctx,
                StatusCodes.Status400BadRequest,
                SourceErrors.Types.Validation,
                "Validation failed",
                "The cursor is not valid.",
                new[] { ("cursor", ex.Message) }),

            _ => throw new UnreachableException(
                $"{nameof(SourceProblem)} must only be called for {nameof(IsSourceFailure)} failures."),
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
            SourceErrors.Types.Validation,
            "Validation failed",
            "One or more fields are invalid.",
            errors);

    /// <summary>
    /// CLR property name → the §13.13 snake_case member the client sent. All
    /// four request records reuse the same member names, so one arm per name
    /// covers every request shape (duplicate constant arms would be a
    /// CS8510 warning — a build error under TreatWarningsAsErrors).
    /// </summary>
    private static string WireFieldName(string propertyName)
        => propertyName switch
        {
            nameof(CreateSourceRequest.TypeId) => "type_id",
            nameof(CreateSourceRequest.DisplayName) => "display_name",
            nameof(CreateSourceRequest.IntervalMinutes) => "interval_minutes",
            nameof(CreateSourceRequest.Config) => "config",
            _ => propertyName,
        };

    /// <summary>Best-effort key hygiene: the DEK copy is wiped as soon as the call ends.</summary>
    private static void Zero(byte[]? dek)
    {
        if (dek is not null)
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }
}
