using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmailAgent.Api.Errors;
using EmailAgent.Api.Pagination;
using EmailAgent.Api.Projections;
using EmailAgent.Core.Audit;
using EmailAgent.Core.Identity;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace EmailAgent.Api.Endpoints;

/// <summary>
/// M1T7: the §13.3 <c>/admin/users</c> namespace — list (cursor-paged per
/// §13.13), read, create (OTP returned exactly once per Q-A3) and OTP
/// reissue. The whole group is <c>[Authorize(Roles = "Admin")]</c>; every
/// 4xx/5xx is an RFC 7807 document (<see cref="ProblemDetailsHelpers.Problem"/>);
/// every successful mutation or read writes its own HMAC-chained audit row.
/// The 401/403 outcomes for anonymous/non-admin callers come from the
/// JwtBearer challenge/forbid events wired in Program.cs (M1T6), so they are
/// problem documents without any endpoint-local code.
/// </summary>
public static class AdminUserEndpoints
{
    /// <summary>The single role the whole namespace requires (§13.3).</summary>
    private const string AdminRole = "Admin";

    /// <summary>§13.12 error classes this namespace emits.</summary>
    private static class ErrorTypes
    {
        public const string Validation = "/errors/validation";
        public const string Unauthorized = "/errors/unauthorized";
        public const string Forbidden = "/errors/forbidden";
        public const string NotFound = "/errors/not-found";
        public const string EmailAlreadyExists = "/errors/email-already-exists";
    }

    /// <summary>
    /// Maps the four admin user routes under <c>/v1/admin/users</c>. A lone
    /// role requirement is exactly <c>[Authorize(Roles = "Admin")]</c>:
    /// anonymous callers are challenged (401 problem), authenticated
    /// non-admins are forbidden (403 problem).
    /// </summary>
    public static IEndpointRouteBuilder MapAdminUserEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/v1/admin/users")
            .WithTags("Admin")
            .RequireAuthorization(policy => policy.RequireRole(AdminRole));

        group.MapGet(string.Empty, ListUsers).WithName("AdminListUsers");
        group.MapPost(string.Empty, CreateUser).WithName("AdminCreateUser");
        group.MapGet("/{id:guid}", GetUser).WithName("AdminGetUser");
        group.MapPost("/{id:guid}/otp", ReissueOtp).WithName("AdminReissueOtp");

        return endpoints;
    }

    /// <summary>
    /// <c>GET /v1/admin/users</c> — cursor-paged list
    /// (<c>?cursor=…&amp;limit=…</c>, default 25, max 100 per §13.13) with the
    /// §13.13 filters <c>filter[email]</c> (citext equality — case
    /// insensitive), <c>filter[role]</c> and <c>filter[status]</c>. The
    /// projection never carries key material; the audit row records the raw
    /// query parameters in <c>metadata_json</c>.
    /// </summary>
    private static async Task<IResult> ListUsers(
        HttpContext ctx,
        EmailAgentDbContext db,
        UserManager<User> userManager,
        IAuditLogger auditLogger,
        CancellationToken ct)
    {
        var actor = await userManager.GetUserAsync(ctx.User);
        if (actor is null)
        {
            return UnauthorizedProblem(ctx);
        }

        if (actor.Status == UserStatus.Disabled)
        {
            return DisabledActorProblem(ctx);
        }

        if (!PaginationQuery.TryParseLimit(
                ctx.Request.Query["limit"].ToString(), out var limit, out var limitError))
        {
            return ValidationProblem(ctx, new[] { ("limit", limitError!) });
        }

        (DateTimeOffset CreatedAt, Guid Id)? after = null;
        var rawCursor = ctx.Request.Query["cursor"].ToString();
        if (!string.IsNullOrEmpty(rawCursor))
        {
            try
            {
                after = Cursor.Decode(rawCursor);
            }
            catch (InvalidCursorException ex)
            {
                return ValidationProblem(ctx, new[] { ("cursor", ex.Message) });
            }
        }

        var emailFilter = ctx.Request.Query["filter[email]"].ToString();
        var roleFilter = ctx.Request.Query["filter[role]"].ToString();
        var statusFilter = ctx.Request.Query["filter[status]"].ToString();

        UserRole? role = null;
        if (!string.IsNullOrEmpty(roleFilter))
        {
            // The round-trip clause rejects numeric literals (`filter[role]=0`)
            // and padded values, keeping the query wire consistent with the
            // create body, whose string enums never accept integers.
            if (!Enum.TryParse<UserRole>(roleFilter, ignoreCase: true, out var parsedRole)
                || !Enum.IsDefined(parsedRole)
                || !string.Equals(parsedRole.ToString(), roleFilter, StringComparison.OrdinalIgnoreCase))
            {
                return ValidationProblem(ctx, new[]
                {
                    ("filter[role]", "must be one of: Admin, User."),
                });
            }

            role = parsedRole;
        }

        UserStatus? status = null;
        if (!string.IsNullOrEmpty(statusFilter))
        {
            if (!Enum.TryParse<UserStatus>(statusFilter, ignoreCase: true, out var parsedStatus)
                || !Enum.IsDefined(parsedStatus)
                || !string.Equals(parsedStatus.ToString(), statusFilter, StringComparison.OrdinalIgnoreCase))
            {
                return ValidationProblem(ctx, new[]
                {
                    ("filter[status]", "must be one of: Pending, Active, Disabled."),
                });
            }

            status = parsedStatus;
        }

        IQueryable<User> query = db.Users.AsNoTracking();

        if (!string.IsNullOrEmpty(emailFilter))
        {
            // users.email is citext — the SQL `=` comparison is
            // case-insensitive without any ToLower() rewrite.
            query = query.Where(u => u.Email == emailFilter);
        }

        if (role is { } roleValue)
        {
            query = query.Where(u => u.Role == roleValue);
        }

        if (status is { } statusValue)
        {
            query = query.Where(u => u.Status == statusValue);
        }

        if (after is { } position)
        {
            // §13.13 ordering: (created_at DESC, id DESC); the cursor page
            // starts strictly after the previous page's last key.
            query = query.Where(u => u.CreatedAt < position.CreatedAt
                || (u.CreatedAt == position.CreatedAt && u.Id < position.Id));
        }

        // limit + 1 rows: one extra row proves a next page exists without a
        // second COUNT query.
        var rows = await query
            .OrderByDescending(u => u.CreatedAt)
            .ThenByDescending(u => u.Id)
            .Take(limit + 1)
            .ToListAsync(ct);

        var hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        string? nextCursor = null;
        if (hasMore)
        {
            var last = rows[^1];
            nextCursor = Cursor.Encode(last.CreatedAt, last.Id);
        }

        await auditLogger.LogAsync(
            ActorEvent(
                ctx, actor, AuditActions.AdminListUsers,
                targetId: null, metadataJson: QueryParametersMetadata(ctx)),
            ct);

        return Results.Ok(new PagedResult<UserSummaryResponse>
        {
            Items = rows.Select(UserSummaryProjection.FromUser).ToList(),
            NextCursor = nextCursor,
        });
    }

    /// <summary>
    /// <c>GET /v1/admin/users/{id}</c> — same projection as the list;
    /// 404 <c>/errors/not-found</c> when the id is unknown.
    /// </summary>
    private static async Task<IResult> GetUser(
        HttpContext ctx,
        Guid id,
        EmailAgentDbContext db,
        UserManager<User> userManager,
        IAuditLogger auditLogger,
        CancellationToken ct)
    {
        var actor = await userManager.GetUserAsync(ctx.User);
        if (actor is null)
        {
            return UnauthorizedProblem(ctx);
        }

        if (actor.Status == UserStatus.Disabled)
        {
            return DisabledActorProblem(ctx);
        }

        var user = await db.Users.FindAsync(new object[] { id }, ct);
        if (user is null)
        {
            return NotFoundProblem(ctx, id);
        }

        await auditLogger.LogAsync(
            ActorEvent(ctx, actor, AuditActions.AdminReadUser, id.ToString(), metadataJson: null),
            ct);

        return Results.Ok(UserSummaryProjection.FromUser(user));
    }

    /// <summary>
    /// <c>POST /v1/admin/users</c> — creates a <c>status=Pending</c>,
    /// <c>must_change_password=true</c> account with a random temporary
    /// password (never returned — the OTP is the only credential the admin
    /// sees) and <b>no key material at all</b> (Q-E6: kdf/dek columns stay
    /// NULL until the user's own set-passphrase). Issues the first OTP via
    /// <see cref="IOtpService.IssueAsync"/> and returns it exactly once
    /// (Q-A3) in the 201 body. Duplicate email → 409
    /// <c>/errors/email-already-exists</c> (citext case-insensitive).
    /// Audits <c>admin_create_user</c> and the create-flow
    /// <c>admin_otp_issued</c> row.
    /// </summary>
    private static async Task<IResult> CreateUser(
        HttpContext ctx,
        EmailAgentDbContext db,
        UserManager<User> userManager,
        IOtpService otpService,
        IAuditLogger auditLogger,
        CancellationToken ct)
    {
        var actor = await userManager.GetUserAsync(ctx.User);
        if (actor is null)
        {
            return UnauthorizedProblem(ctx);
        }

        if (actor.Status == UserStatus.Disabled)
        {
            return DisabledActorProblem(ctx);
        }

        var (request, bodyError) = await ReadJsonBodyAsync<CreateUserRequest>(ctx, ct);
        if (bodyError is not null || request is null)
        {
            return bodyError ?? BodyRequired(ctx);
        }

        var validation = new CreateUserRequestValidator().Validate(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ctx, validation);
        }

        var email = request.Email!.Trim();
        var role = request.Role;

        if (await db.Users.AnyAsync(u => u.Email == email, ct))
        {
            return EmailAlreadyExists(ctx, email);
        }

        // Pre-flight (review WARNING): the Identity role rows are provisioned
        // by the environment (seed-dev.sh for dev, the test harness for
        // tests). Refuse BEFORE inserting so a provisioning gap can never
        // strand an orphan user row that every retry then rejects as 409 —
        // there is no update/delete endpoint before M7a to recover with.
        var roleNormalizedName = role.ToString().ToUpperInvariant();
        if (!await db.IdentityRoles.AnyAsync(
                r => r.NormalizedName == roleNormalizedName, ct))
        {
            throw new InvalidOperationException(
                $"the '{role}' role is not provisioned in AspNetRoles; "
                + "run the environment bootstrap (scripts/seed-dev.sh) first.");
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserName = email,
            Role = role,
            Status = UserStatus.Pending,
            MustChangePassword = true,
            CreatedAt = DateTimeOffset.UtcNow,
            // KdfSalt / KdfParams / DekWrapped / DekNonce stay NULL (Q-E6).
        };

        // The temporary password satisfies the Q-A5 policy but is never
        // disclosed: the OTP below is the only credential this flow returns.
        var createResult = await userManager.CreateAsync(user, GenerateTemporaryPassword());
        if (!createResult.Succeeded)
        {
            // The pre-check above can race a concurrent create; re-checking
            // turns the validator's duplicate answer into the same 409. The
            // DB UNIQUE constraint remains the final backstop (a simultaneous
            // insert surfaces as 500 — see the task's Risks).
            if (await db.Users.AnyAsync(u => u.Email == email, ct))
            {
                return EmailAlreadyExists(ctx, email);
            }

            return ValidationProblem(ctx, createResult.Errors
                .Select(error => ("body", error.Description))
                .ToList());
        }

        var roleResult = await userManager.AddToRoleAsync(user, role.ToString());
        if (!roleResult.Succeeded)
        {
            // Compensating delete (review WARNING): never leave a
            // created-but-unassigned user behind — a retry would be stuck on
            // 409 with no recovery path.
            await userManager.DeleteAsync(user);
            throw new InvalidOperationException(
                $"role assignment failed: {string.Join(", ", roleResult.Errors.Select(e => e.Code))}");
        }

        IssuedOtp issued;
        try
        {
            issued = await otpService.IssueAsync(user.Id, actor.Id, ct);

            // The audit writes ride the same compensation (re-review
            // suggestion): a failure here must not strand a created user
            // with a lost OTP — the retry would be stuck on 409. Deleting
            // the user cannot be blocked by these rows: their actor is the
            // admin (FK) and target_id is plain text.
            await auditLogger.LogAsync(
                ActorEvent(
                    ctx, actor, AuditActions.AdminCreateUser, user.Id.ToString(),
                    JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["created_user_id"] = user.Id,
                        ["email"] = email,
                        ["role"] = role.ToString(),
                    })),
                ct);

            // `source` distinguishes this create-flow issuance from the
            // reissue flow in audit queries (task step 7).
            await auditLogger.LogAsync(
                ActorEvent(
                    ctx, actor, AuditActions.AdminOtpIssued, user.Id.ToString(),
                    JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["target_user_id"] = user.Id,
                        ["source"] = "create",
                    })),
                ct);
        }
        catch
        {
            // Compensation (review WARNING): a user row with no credential
            // path — or an unacknowledged create — is worse than a failed
            // request; roll the account back so the admin can simply retry.
            await userManager.DeleteAsync(user);
            throw;
        }

        return Results.Created($"/v1/admin/users/{user.Id}", new CreateUserResponse
        {
            Id = user.Id,
            Email = email,
            Role = role,
            Status = UserStatus.Pending,
            Otp = issued.RawOtp,
            OtpExpiresAt = issued.ExpiresAt,
        });
    }

    /// <summary>
    /// <c>POST /v1/admin/users/{id}/otp</c> — reissues the OTP through
    /// <see cref="IOtpService.RegenerateAsync"/>, which atomically revokes
    /// every prior unused OTP (Q-A3 / SQ-6). 404 when the user does not
    /// exist, 400 for a Disabled account. Audits <c>admin_otp_issued</c>
    /// with <c>source: "reissue"</c>.
    /// </summary>
    private static async Task<IResult> ReissueOtp(
        HttpContext ctx,
        Guid id,
        EmailAgentDbContext db,
        UserManager<User> userManager,
        IOtpService otpService,
        IAuditLogger auditLogger,
        CancellationToken ct)
    {
        var actor = await userManager.GetUserAsync(ctx.User);
        if (actor is null)
        {
            return UnauthorizedProblem(ctx);
        }

        if (actor.Status == UserStatus.Disabled)
        {
            return DisabledActorProblem(ctx);
        }

        var user = await db.Users.FindAsync(new object[] { id }, ct);
        if (user is null)
        {
            return NotFoundProblem(ctx, id);
        }

        if (user.Status == UserStatus.Disabled)
        {
            return ProblemDetailsHelpers.Problem(
                ctx,
                StatusCodes.Status400BadRequest,
                ErrorTypes.Validation,
                "Validation failed",
                "A disabled account cannot be issued a one-time password.");
        }

        var issued = await otpService.RegenerateAsync(user.Id, actor.Id, ct);

        await auditLogger.LogAsync(
            ActorEvent(
                ctx, actor, AuditActions.AdminOtpIssued, user.Id.ToString(),
                JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["target_user_id"] = user.Id,
                    ["source"] = "reissue",
                })),
            ct);

        return Results.Ok(new OtpResponse
        {
            Otp = issued.RawOtp,
            OtpExpiresAt = issued.ExpiresAt,
        });
    }

    /// <summary>
    /// A random 16-char temporary password meeting the Q-A5 policy
    /// (≥ 12 chars, upper + lower + digit + symbol — Program.cs password
    /// options). Cryptographic randomness only; the value never leaves the
    /// process (the OTP is the disclosed credential).
    /// </summary>
    private static string GenerateTemporaryPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%^&*()-_=+";

        var password = new char[20];
        var index = 0;
        Fill(password, ref index, upper, 5);
        Fill(password, ref index, lower, 5);
        Fill(password, ref index, digits, 5);
        Fill(password, ref index, symbols, 5);

        // Fisher-Yates so the class positions are not predictable.
        for (var i = password.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (password[i], password[j]) = (password[j], password[i]);
        }

        return new string(password);

        static void Fill(char[] target, ref int index, string pool, int count)
        {
            for (var i = 0; i < count; i++)
            {
                target[index++] = pool[RandomNumberGenerator.GetInt32(pool.Length)];
            }
        }
    }

    /// <summary><c>metadata_json</c> for <c>admin_list_users</c> — the raw query parameters.</summary>
    private static string QueryParametersMetadata(HttpContext ctx)
    {
        var parameters = new Dictionary<string, string>();
        foreach (var (key, values) in ctx.Request.Query)
        {
            parameters[key] = values.ToString();
        }

        return JsonSerializer.Serialize(parameters);
    }

    private static AuditEvent ActorEvent(
        HttpContext ctx,
        User actor,
        string action,
        string? targetId,
        string? metadataJson)
        => new(
            ActorUserId: actor.Id,
            ActorRole: actor.Role.ToString(),
            Action: action,
            TargetType: targetId is null ? null : "user",
            TargetId: targetId,
            MetadataJson: metadataJson,
            Ip: ctx.Connection.RemoteIpAddress);

    private static IResult UnauthorizedProblem(HttpContext ctx)
        => ProblemDetailsHelpers.Problem(
            ctx,
            StatusCodes.Status401Unauthorized,
            ErrorTypes.Unauthorized,
            "Unauthorized",
            "The access token does not match a live account.");

    /// <summary>
    /// Review WARNING / M1T6 parity: a still-valid JWT must not keep admin
    /// powers for a Disabled account — least of all the OTP reissue, which
    /// mints a fresh login credential. Checked as the first gate of every
    /// handler (the role gate above only proves the claim, not the status).
    /// </summary>
    private static IResult DisabledActorProblem(HttpContext ctx)
        => ProblemDetailsHelpers.Problem(
            ctx,
            StatusCodes.Status403Forbidden,
            ErrorTypes.Forbidden,
            "Forbidden",
            "This account is disabled.");

    private static IResult NotFoundProblem(HttpContext ctx, Guid id)
        => ProblemDetailsHelpers.Problem(
            ctx,
            StatusCodes.Status404NotFound,
            ErrorTypes.NotFound,
            "Not Found",
            $"No user with id {id}.");

    private static IResult EmailAlreadyExists(HttpContext ctx, string email)
        => ProblemDetailsHelpers.Problem(
            ctx,
            StatusCodes.Status409Conflict,
            ErrorTypes.EmailAlreadyExists,
            "Conflict",
            $"A user with the email '{email}' already exists.");

    private static IResult ValidationProblem(
        HttpContext ctx,
        FluentValidation.Results.ValidationResult validation)
        => ValidationProblem(
            ctx,
            validation.Errors
                .Select(error => (Name: WireFieldName(error.PropertyName), Reason: error.ErrorMessage))
                .ToList());

    /// <summary>
    /// CLR property name → the §13.12 <c>name</c> the client actually sent
    /// (the wire fields are snake_case: validation failures name
    /// <c>email</c>, not <c>Email</c>). Unknown names (e.g. the <c>body</c>
    /// sentinel) pass through.
    /// </summary>
    private static string WireFieldName(string propertyName)
        => propertyName switch
        {
            nameof(CreateUserRequest.Email) => "email",
            nameof(CreateUserRequest.Role) => "role",
            _ => propertyName,
        };

    private static IResult ValidationProblem(
        HttpContext ctx,
        IReadOnlyList<(string Name, string Reason)> errors)
        => ProblemDetailsHelpers.Problem(
            ctx,
            StatusCodes.Status400BadRequest,
            ErrorTypes.Validation,
            "Validation failed",
            "One or more fields are invalid.",
            errors);

    private static IResult BodyRequired(HttpContext ctx)
        => ValidationProblem(ctx, new[] { ("body", "A JSON request body is required.") });

    private static readonly JsonSerializerOptions BodySerializerOptions =
        new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Manual body read — the M1T6 idiom (AccountEndpoints): a missing or
    /// malformed JSON payload must be a §13.12 400 problem document, never
    /// the framework binder's empty-bodied rejection. Exactly one tuple
    /// element is set: non-null <c>Body</c> paired with null <c>Error</c>,
    /// or the reverse.
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
            return (null, ProblemDetailsHelpers.Problem(
                ctx,
                StatusCodes.Status400BadRequest,
                ErrorTypes.Validation,
                "Validation failed",
                "The request body is not valid JSON.",
                new[] { ("body", ex.Message) }));
        }
    }
}