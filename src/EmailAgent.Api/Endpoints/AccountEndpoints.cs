using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmailAgent.Api.Auth;
using EmailAgent.Api.Errors;
using EmailAgent.Core.Audit;
using EmailAgent.Core.Errors;
using EmailAgent.Core.Identity;
using EmailAgent.Core.Options;
using EmailAgent.Core.Security;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EmailAgent.Api.Endpoints;

/// <summary>
/// M1T6: the §13.3 <c>/account/*</c> namespace — login, otp-login,
/// set-passphrase, change-password, refresh and logout, all mounted under
/// the §13.7 <c>/v1</c> prefix. Every 4xx/5xx is an RFC 7807 document
/// (<see cref="ProblemDetailsHelpers.Problem"/>), every event writes an
/// HMAC-chained audit row, and the Q-O7 login budget plus the §13.14 header
/// rules come from <see cref="RateLimitPolicies"/> and
/// <see cref="AuthenticatedRateLimitMiddleware"/>.
/// </summary>
public static class AccountEndpoints
{
    /// <summary>Login-state header read by the M1T8 SPA (task step 4).</summary>
    public const string LoginStateHeader = "X-Login-State";

    /// <summary>Value of <see cref="LoginStateHeader"/> for first-login sessions.</summary>
    public const string MustCompleteFirstLoginState = "must-complete-first-login";

    /// <summary>§13.12 error classes this namespace emits (generic set).</summary>
    private static class ErrorTypes
    {
        public const string Validation = "/errors/validation";
        public const string Unauthorized = "/errors/unauthorized";
        public const string Forbidden = "/errors/forbidden";
        public const string Conflict = "/errors/conflict";
        public const string RateLimited = "/errors/rate-limited";
        public const string NoRefreshCookie = "/errors/no-refresh-cookie";
        public const string PassphraseAlreadySet = "/errors/passphrase-already-set";
        public const string CredentialMismatch = "/errors/credential-mismatch";
    }

    /// <summary>
    /// Maps the six account routes. Login/otp-login opt into the
    /// per-account Q-O7 policy; the per-IP half of Q-O7 rides the
    /// <c>RateLimiterOptions.GlobalLimiter</c> registered by
    /// <see cref="RateLimitPolicies.AddRateLimitPolicies"/> — the framework's
    /// rate-limiting middleware honors exactly one
    /// <c>EnableRateLimiting</c> policy per endpoint (it reads the last
    /// metadata entry), so the two budgets are expressed as the global +
    /// endpoint chain rather than two stacked attributes.
    /// </summary>
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/v1/account").WithTags("Account");

        group.MapPost("/login", Login)
            .WithName("AccountLogin")
            .RequireRateLimiting(RateLimitPolicies.LoginPerAccount);

        group.MapPost("/otp-login", OtpLogin)
            .WithName("AccountOtpLogin")
            .RequireRateLimiting(RateLimitPolicies.LoginPerAccount);

        // First-login bootstrap pair — JWT-authenticated (the OTP-derived or
        // password session is the credential; §13.3 "no role required").
        group.MapPost("/set-passphrase", SetPassphrase)
            .WithName("AccountSetPassphrase")
            .RequireAuthorization();

        group.MapPost("/change-password", ChangePassword)
            .WithName("AccountChangePassword")
            .RequireAuthorization();

        // Refresh/logout are cookie-based: no JWT, no role (§13.10/§13.11).
        // The Origin gate for /v1/account/refresh is the path-scoped inline
        // middleware in Program.cs (M1T4 task step 8).
        group.MapPost("/refresh", Refresh)
            .WithName("AccountRefresh");

        group.MapPost("/logout", Logout)
            .WithName("AccountLogout");

        return endpoints;
    }

    /// <summary>
    /// <c>POST /v1/account/login</c> — email + password form → 200
    /// <see cref="TokenResponse"/> + <c>ea_refresh</c> cookie. Always 200 on
    /// success: the SPA reads <c>must_complete_first_login</c> /
    /// <c>X-Login-State</c> to route the first-login flow (task step 4).
    /// </summary>
    private static async Task<IResult> Login(
        HttpContext ctx,
        UserManager<User> userManager,
        SignInManager<User> signInManager,
        IRefreshTokenStore refreshTokenStore,
        IJwtTokenIssuer tokenIssuer,
        IAuditLogger auditLogger,
        IOptions<AuthOptions> authOptions,
        CancellationToken ct)
    {
        // Client-error guard: ReadFormAsync on a non-form body throws, which
        // would misclassify a bad request as a 500 (review finding 2).
        if (!ctx.Request.HasFormContentType)
        {
            return FormBodyRequired(ctx, "email, password");
        }

        var form = await ctx.Request.ReadFormAsync(ct);
        var request = new LoginRequest
        {
            Email = form["email"].ToString(),
            Password = form["password"].ToString(),
        };

        var ip = ctx.Connection.RemoteIpAddress;
        var user = string.IsNullOrWhiteSpace(request.Email)
            ? null
            : await userManager.FindByEmailAsync(request.Email);

        var signIn = user is null
            ? null
            : await signInManager.CheckPasswordSignInAsync(
                user, request.Password, lockoutOnFailure: false);

        if (user is null || signIn is null || !signIn.Succeeded)
        {
            // AC: login_failure carries the email in metadata_json — never in
            // actor_user_id (the account might not exist).
            await auditLogger.LogAsync(
                new AuditEvent(
                    ActorUserId: user?.Id,
                    ActorRole: user?.Role.ToString(),
                    Action: AuditActions.LoginFailure,
                    TargetType: "user",
                    TargetId: user?.Id.ToString(),
                    MetadataJson: EmailMetadata(request.Email),
                    Ip: ip),
                ct);

            return ProblemDetailsHelpers.Problem(
                ctx,
                StatusCodes.Status401Unauthorized,
                ErrorTypes.Unauthorized,
                "Unauthorized",
                "Invalid email or password.");
        }

        // Review WARNING: a Disabled account never reaches token issuance —
        // the same 403 gate otp-login applies. Checked AFTER the password so
        // a wrong password on a disabled account stays a generic 401 (no
        // status oracle for an unauthenticated prober); the blocked attempt
        // itself is audited as login_failure with the reason.
        if (user.Status == UserStatus.Disabled)
        {
            await auditLogger.LogAsync(
                new AuditEvent(
                    ActorUserId: user.Id,
                    ActorRole: user.Role.ToString(),
                    Action: AuditActions.LoginFailure,
                    TargetType: "user",
                    TargetId: user.Id.ToString(),
                    MetadataJson: DisabledAccountMetadata(request.Email),
                    Ip: ip),
                ct);

            return ProblemDetailsHelpers.Problem(
                ctx,
                StatusCodes.Status403Forbidden,
                ErrorTypes.Forbidden,
                "Forbidden",
                "This account is disabled.");
        }

        var issued = await refreshTokenStore.IssueAsync(
            user.Id,
            ctx.Request.Headers.UserAgent.ToString(),
            ip?.ToString() ?? string.Empty,
            ct);

        var accessToken = tokenIssuer.IssueAccessToken(
            user, await userManager.GetRolesAsync(user));
        RefreshCookieBinder.Write(ctx, issued.RawToken, issued.Record.ExpiresAt);

        await auditLogger.LogAsync(
            new AuditEvent(
                ActorUserId: user.Id,
                ActorRole: user.Role.ToString(),
                Action: AuditActions.LoginSuccess,
                TargetType: "user",
                TargetId: user.Id.ToString(),
                MetadataJson: EmailMetadata(request.Email),
                Ip: ip),
            ct);

        // must_complete_first_login = must_change_password OR dek_wrapped IS
        // NULL (Q-E6: key material does not exist until set-passphrase).
        var mustCompleteFirstLogin = user.MustChangePassword || user.DekWrapped is null;
        if (mustCompleteFirstLogin)
        {
            ctx.Response.Headers[LoginStateHeader] = MustCompleteFirstLoginState;
        }

        return Results.Ok(new TokenResponse
        {
            AccessToken = accessToken,
            ExpiresIn = (int)authOptions.Value.AccessTokenTtl.TotalSeconds,
            MustCompleteFirstLogin = mustCompleteFirstLogin,
        });
    }

    /// <summary>
    /// <c>POST /v1/account/otp-login</c> — email + OTP form → same response
    /// shape as login. Marks the user Active, forces the first-login password
    /// change, and reports <c>must_complete_first_login</c> per the same
    /// formula as <see cref="Login"/> (task step 5).
    /// </summary>
    private static async Task<IResult> OtpLogin(
        HttpContext ctx,
        UserManager<User> userManager,
        IOtpService otpService,
        IRefreshTokenStore refreshTokenStore,
        IJwtTokenIssuer tokenIssuer,
        IAuditLogger auditLogger,
        IOptions<AuthOptions> authOptions,
        CancellationToken ct)
    {
        // Client-error guard — see Login.
        if (!ctx.Request.HasFormContentType)
        {
            return FormBodyRequired(ctx, "email, otp");
        }

        var form = await ctx.Request.ReadFormAsync(ct);
        var request = new OtpLoginRequest
        {
            Email = form["email"].ToString(),
            Otp = form["otp"].ToString(),
        };

        var ip = ctx.Connection.RemoteIpAddress;
        var user = string.IsNullOrWhiteSpace(request.Email)
            ? null
            : await userManager.FindByEmailAsync(request.Email);

        if (user is null)
        {
            await auditLogger.LogAsync(
                new AuditEvent(
                    ActorUserId: null,
                    ActorRole: null,
                    Action: AuditActions.OtpLoginFailure,
                    TargetType: "user",
                    TargetId: null,
                    MetadataJson: EmailMetadata(request.Email),
                    Ip: ip),
                ct);

            return OtpProblem(ctx, "Invalid email or one-time password.");
        }

        if (user.Status == UserStatus.Disabled)
        {
            return ProblemDetailsHelpers.Problem(
                ctx,
                StatusCodes.Status403Forbidden,
                ErrorTypes.Forbidden,
                "Forbidden",
                "This account is disabled.");
        }

        var verified = await otpService.VerifyAsync(user.Id, request.Otp, ct);
        if (!verified)
        {
            await auditLogger.LogAsync(
                new AuditEvent(
                    ActorUserId: user.Id,
                    ActorRole: user.Role.ToString(),
                    Action: AuditActions.OtpLoginFailure,
                    TargetType: "user",
                    TargetId: user.Id.ToString(),
                    MetadataJson: EmailMetadata(request.Email),
                    Ip: ip),
                ct);

            return OtpProblem(ctx, "Invalid email or one-time password.");
        }

        await auditLogger.LogAsync(
            new AuditEvent(
                ActorUserId: user.Id,
                ActorRole: user.Role.ToString(),
                Action: AuditActions.OtpLoginSuccess,
                TargetType: "user",
                TargetId: user.Id.ToString(),
                MetadataJson: EmailMetadata(request.Email),
                Ip: ip),
            ct);

        // First OTP login: Pending → Active and the password change is
        // forced. The DEK deliberately stays NULL — collecting the passphrase
        // is the SPA's next screen (Q-E6; task step 5).
        user.Status = UserStatus.Active;
        user.MustChangePassword = true;
        var updated = await userManager.UpdateAsync(user);
        if (!updated.Succeeded)
        {
            throw new InvalidOperationException(
                $"OTP-login state update failed: {string.Join(", ", updated.Errors.Select(e => e.Code))}");
        }

        var issued = await refreshTokenStore.IssueAsync(
            user.Id,
            ctx.Request.Headers.UserAgent.ToString(),
            ip?.ToString() ?? string.Empty,
            ct);

        var accessToken = tokenIssuer.IssueAccessToken(
            user, await userManager.GetRolesAsync(user));
        RefreshCookieBinder.Write(ctx, issued.RawToken, issued.Record.ExpiresAt);

        // Kept identical to /login (in practice always true here because
        // MustChangePassword was just forced) so the SPA branches on one
        // formula (task step 5).
        var mustCompleteFirstLogin = user.MustChangePassword || user.DekWrapped is null;
        if (mustCompleteFirstLogin)
        {
            ctx.Response.Headers[LoginStateHeader] = MustCompleteFirstLoginState;
        }

        return Results.Ok(new TokenResponse
        {
            AccessToken = accessToken,
            ExpiresIn = (int)authOptions.Value.AccessTokenTtl.TotalSeconds,
            MustCompleteFirstLogin = mustCompleteFirstLogin,
        });
    }

    /// <summary>
    /// <c>POST /v1/account/set-passphrase</c> — first-time KEK passphrase
    /// set (Q-A4/Q-E1 two inputs, Q-E6 DEK bootstrap). Generates a fresh
    /// 16-byte salt and a fresh 32-byte DEK, wraps the DEK under the
    /// Argon2id KEK, and persists <c>kdf_salt</c>, <c>kdf_params</c>,
    /// <c>dek_wrapped</c> and <c>dek_nonce</c> together. First-time only:
    /// a repeat call is 409 <c>/errors/passphrase-already-set</c> — the
    /// re-wrap-on-change endpoint lives at M6a's
    /// <c>POST /user/account/change-passphrase</c> (Q-E3).
    /// </summary>
    private static async Task<IResult> SetPassphrase(
        HttpContext ctx,
        UserManager<User> userManager,
        IEncryptionService encryptionService,
        IAuditLogger auditLogger,
        CancellationToken ct)
    {
        var user = await userManager.GetUserAsync(ctx.User);
        if (user is null)
        {
            return ProblemDetailsHelpers.Problem(
                ctx,
                StatusCodes.Status401Unauthorized,
                ErrorTypes.Unauthorized,
                "Unauthorized",
                "The access token does not match a live account.");
        }

        // Review WARNING: a still-valid JWT must not keep working for a
        // Disabled account (status gate parity with login/otp-login).
        if (user.Status == UserStatus.Disabled)
        {
            return ProblemDetailsHelpers.Problem(
                ctx,
                StatusCodes.Status403Forbidden,
                ErrorTypes.Forbidden,
                "Forbidden",
                "This account is disabled.");
        }

        if (user.DekWrapped is not null)
        {
            return ProblemDetailsHelpers.Problem(
                ctx,
                StatusCodes.Status409Conflict,
                ErrorTypes.PassphraseAlreadySet,
                "Conflict",
                "A passphrase is already set for this account. Use POST "
                    + "/user/account/change-passphrase to change it (available from M6a); "
                    + "the existing data-encryption key is re-wrapped, not replaced (Q-E3).");
        }

        // The body is read manually (task step 1's AC): a missing or
        // malformed JSON payload must be a §13.12 400 problem document, not
        // the framework binder's empty-bodied rejection.
        var (request, bodyError) = await ReadJsonBodyAsync<SetPassphraseRequest>(ctx, ct);
        if (bodyError is not null || request is null)
        {
            return bodyError ?? BodyRequired(ctx);
        }

        var validation = new SetPassphraseRequestValidator().Validate(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ctx, validation);
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        var kdfParams = KdfParams.Default();

        WrappedDek wrapped;
        try
        {
            // Q-E5: the per-user zero-wait KEK mutex inside the service —
            // a concurrent derivation for the same user throws and maps to
            // 429 below; Q-E6: the DEK is minted and wrapped right here,
            // never before.
            wrapped = encryptionService.GenerateAndWrapDek(
                user.Id, request.Passphrase!, salt, kdfParams);
        }
        catch (KekDerivationBusyException)
        {
            // §13.14/Q-E5: the SPA renders a Retry-After countdown on the
            // set-passphrase control (no automatic retry).
            ctx.Response.Headers.RetryAfter = "1";
            return ProblemDetailsHelpers.Problem(
                ctx,
                StatusCodes.Status429TooManyRequests,
                ErrorTypes.RateLimited,
                "Too Many Requests",
                "A key derivation for this account is already in progress; retry shortly.");
        }
        catch (InvalidPassphraseException)
        {
            // Defensive mapping per the M1 contract: a passphrase that fails
            // GCM authentication is a credential mismatch (401), never a 500.
            return ProblemDetailsHelpers.Problem(
                ctx,
                StatusCodes.Status401Unauthorized,
                ErrorTypes.CredentialMismatch,
                "Unauthorized",
                "The passphrase is not valid.");
        }

        // Q-E6: all four key-material columns are written together — the row
        // moves from "no crypto state" to "fully initialized" in one save.
        user.KdfSalt = salt;
        user.KdfParams = JsonSerializer.SerializeToDocument(kdfParams);
        user.DekWrapped = wrapped.Wrapped;
        user.DekNonce = wrapped.Nonce;

        var updated = await userManager.UpdateAsync(user);
        if (!updated.Succeeded)
        {
            throw new InvalidOperationException(
                $"set-passphrase persistence failed: {string.Join(", ", updated.Errors.Select(e => e.Code))}");
        }

        await auditLogger.LogAsync(
            new AuditEvent(
                ActorUserId: user.Id,
                ActorRole: user.Role.ToString(),
                Action: AuditActions.SetPassphrase,
                TargetType: "user",
                TargetId: user.Id.ToString(),
                MetadataJson: "{\"kdf\":\"argon2id\"}",
                Ip: ctx.Connection.RemoteIpAddress),
            ct);

        return Results.NoContent();
    }

    /// <summary>
    /// <c>POST /v1/account/change-password</c> — task step 7. While
    /// <c>must_change_password</c> is true the body is
    /// <c>{ new_password, new_password_confirm }</c> only (the session is the
    /// credential — Alice never knew the temporary password set by admin/OTP
    /// flows); otherwise <c>old_password</c> is required and verified through
    /// <c>UserManager.ChangePasswordAsync</c>.
    /// </summary>
    private static async Task<IResult> ChangePassword(
        HttpContext ctx,
        UserManager<User> userManager,
        IAuditLogger auditLogger,
        CancellationToken ct)
    {
        var user = await userManager.GetUserAsync(ctx.User);
        if (user is null)
        {
            return ProblemDetailsHelpers.Problem(
                ctx,
                StatusCodes.Status401Unauthorized,
                ErrorTypes.Unauthorized,
                "Unauthorized",
                "The access token does not match a live account.");
        }

        // Review WARNING: a still-valid JWT must not keep working for a
        // Disabled account (status gate parity with login/otp-login).
        if (user.Status == UserStatus.Disabled)
        {
            return ProblemDetailsHelpers.Problem(
                ctx,
                StatusCodes.Status403Forbidden,
                ErrorTypes.Forbidden,
                "Forbidden",
                "This account is disabled.");
        }

        // Manual body read — see SetPassphrase (missing/malformed JSON → §13.12 400).
        var (request, bodyError) = await ReadJsonBodyAsync<ChangePasswordRequest>(ctx, ct);
        if (bodyError is not null || request is null)
        {
            return bodyError ?? BodyRequired(ctx);
        }

        var firstLogin = user.MustChangePassword;
        var validation = new ChangePasswordRequestValidator(firstLogin).Validate(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ctx, validation);
        }

        if (firstLogin)
        {
            // §6 pins users.password_hash NOT NULL, so Identity's
            // RemovePasswordAsync + AddPasswordAsync pairing (task step 7)
            // cannot run: RemovePasswordAsync transiently writes NULL and
            // the insert is rejected by the column constraint. The
            // observable contract is identical — run the same password
            // validators AddPasswordAsync would, then replace the hash in
            // the single flag-flip save below (no old_password consulted:
            // the session is the credential).
            foreach (var validator in userManager.PasswordValidators)
            {
                var passwordValidation = await validator.ValidateAsync(
                    userManager, user, request.NewPassword!);
                if (!passwordValidation.Succeeded)
                {
                    return ValidationProblem(
                        ctx,
                        passwordValidation.Errors
                            .Select(error => (Name: error.Code, Reason: error.Description))
                            .ToList());
                }
            }

            user.PasswordHash = userManager.PasswordHasher.HashPassword(
                user, request.NewPassword!);
        }
        else
        {
            var result = await userManager.ChangePasswordAsync(
                user, request.OldPassword!, request.NewPassword!);

            if (!result.Succeeded)
            {
                // A wrong old_password and Identity policy violations both
                // land here as field-level §13.12 errors (400, task step 7).
                return ValidationProblem(
                    ctx,
                    result.Errors
                        .Select(error => (Name: error.Code, Reason: error.Description))
                        .ToList());
            }
        }

        user.MustChangePassword = false;
        var updated = await userManager.UpdateAsync(user);
        if (!updated.Succeeded)
        {
            throw new InvalidOperationException(
                $"change-password flag update failed: {string.Join(", ", updated.Errors.Select(e => e.Code))}");
        }

        await auditLogger.LogAsync(
            new AuditEvent(
                ActorUserId: user.Id,
                ActorRole: user.Role.ToString(),
                Action: AuditActions.ChangePassword,
                TargetType: "user",
                TargetId: user.Id.ToString(),
                MetadataJson: JsonSerializer.Serialize(
                    new Dictionary<string, bool> { ["first_login"] = firstLogin }),
                Ip: ctx.Connection.RemoteIpAddress),
            ct);

        return Results.NoContent();
    }

    /// <summary>
    /// <c>POST /v1/account/refresh</c> — task step 8. Cookie-based (no JWT):
    /// look up by SHA-256 hash, rotate atomically, write the successor
    /// cookie and return a fresh access token. Reuse and expiry both map to
    /// RFC 7807 401; the reuse evidence row is written inside the store
    /// (M1T4), so the endpoint must not emit a duplicate — it only clears the
    /// cookie (task step 8).
    /// </summary>
    private static async Task<IResult> Refresh(
        HttpContext ctx,
        UserManager<User> userManager,
        IRefreshTokenStore refreshTokenStore,
        IJwtTokenIssuer tokenIssuer,
        IAuditLogger auditLogger,
        IOptions<AuthOptions> authOptions,
        CancellationToken ct)
    {
        var rawToken = RefreshCookieBinder.ReadRaw(ctx);
        if (rawToken is null)
        {
            return ProblemDetailsHelpers.Problem(
                ctx,
                StatusCodes.Status401Unauthorized,
                ErrorTypes.NoRefreshCookie,
                "Unauthorized",
                "No refresh cookie was presented.");
        }

        var tokenHash = RefreshTokenHash(rawToken);
        if (tokenHash is null)
        {
            RefreshCookieBinder.Clear(ctx);
            return ProblemDetailsHelpers.Problem(
                ctx,
                StatusCodes.Status401Unauthorized,
                ErrorTypes.Unauthorized,
                "Unauthorized",
                "The refresh cookie is not valid.");
        }

        var record = await refreshTokenStore.FindByHashAsync(tokenHash, ct);
        if (record is null)
        {
            // Nothing revocable behind this cookie — clear it so a dead
            // value is not re-presented for its full TTL (parity with the
            // sibling refusal branches).
            RefreshCookieBinder.Clear(ctx);
            return ProblemDetailsHelpers.Problem(
                ctx,
                StatusCodes.Status401Unauthorized,
                ErrorTypes.Unauthorized,
                "Unauthorized",
                "The refresh cookie is not valid.");
        }

        // Resolve the account BEFORE rotating so a deleted or Disabled
        // account's session is cut without minting a successor token. Two
        // review-driven nuances:
        //  - consumed/revoked/expired tokens (storeMustJudge) still fall
        //    through to RotateAsync: the store's evidence principle (family
        //    revocation + refresh_reuse_detected row) must fire even during
        //    a disabled window — those tokens are refused by the store with
        //    the existing 401 mappings, never rotated;
        //  - only a LIVE, never-used token on a blocked account
        //    short-circuits here (401 deleted / 403 disabled, cookie cleared).
        var user = await userManager.FindByIdAsync(record.UserId.ToString());
        var storeMustJudge = record.UsedAt is not null
            || record.RevokedAt is not null
            || record.ExpiresAt <= DateTimeOffset.UtcNow;

        if (user is null || (user.Status == UserStatus.Disabled && !storeMustJudge))
        {
            RefreshCookieBinder.Clear(ctx);
            return user is null
                ? ProblemDetailsHelpers.Problem(
                    ctx,
                    StatusCodes.Status401Unauthorized,
                    ErrorTypes.Unauthorized,
                    "Unauthorized",
                    "The refresh cookie is not valid.")
                : ProblemDetailsHelpers.Problem(
                    ctx,
                    StatusCodes.Status403Forbidden,
                    ErrorTypes.Forbidden,
                    "Forbidden",
                    "This account is disabled.");
        }

        IssuedRefreshToken issued;
        try
        {
            issued = await refreshTokenStore.RotateAsync(record, ct);
        }
        catch (RefreshTokenReuseException)
        {
            // The store already revoked the family and wrote
            // refresh_reuse_detected (M1T4 step 3) — clear the cookie and
            // refuse without a second audit row (task step 8).
            RefreshCookieBinder.Clear(ctx);
            return ProblemDetailsHelpers.Problem(
                ctx,
                StatusCodes.Status401Unauthorized,
                ErrorTypes.Unauthorized,
                "Unauthorized",
                "This refresh token has already been used; the session family has been revoked.");
        }
        catch (RefreshTokenExpiredException)
        {
            RefreshCookieBinder.Clear(ctx);
            return ProblemDetailsHelpers.Problem(
                ctx,
                StatusCodes.Status401Unauthorized,
                ErrorTypes.Unauthorized,
                "Unauthorized",
                "This refresh token has expired; re-authentication is required.");
        }

        var accessToken = tokenIssuer.IssueAccessToken(
            user, await userManager.GetRolesAsync(user));
        RefreshCookieBinder.Write(ctx, issued.RawToken, issued.Record.ExpiresAt);

        await auditLogger.LogAsync(
            new AuditEvent(
                ActorUserId: user.Id,
                ActorRole: user.Role.ToString(),
                Action: AuditActions.RefreshSuccess,
                TargetType: "refresh_token",
                TargetId: record.Id.ToString(),
                MetadataJson: JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["family_id"] = record.FamilyId.ToString(),
                }),
                Ip: ctx.Connection.RemoteIpAddress),
            ct);

        // No must_complete_first_login member: task step 8's response shape
        // is { access_token, expires_in } (TokenResponse omits null members).
        return Results.Ok(new TokenResponse
        {
            AccessToken = accessToken,
            ExpiresIn = (int)authOptions.Value.AccessTokenTtl.TotalSeconds,
            MustCompleteFirstLogin = null,
        });
    }

    /// <summary>
    /// <c>POST /v1/account/logout</c> — task step 9. Revokes the presented
    /// refresh token, audits <c>logout</c> (actor resolved through the token
    /// so the milestone's non-null actor/ip/hmac row assertion holds), clears
    /// the cookie and returns 204. Cookie-less calls are idempotent 204s.
    /// </summary>
    private static async Task<IResult> Logout(
        HttpContext ctx,
        UserManager<User> userManager,
        IRefreshTokenStore refreshTokenStore,
        IAuditLogger auditLogger,
        CancellationToken ct)
    {
        var rawToken = RefreshCookieBinder.ReadRaw(ctx);
        if (rawToken is null)
        {
            return Results.NoContent();
        }

        var tokenHash = RefreshTokenHash(rawToken);
        if (tokenHash is null)
        {
            // An unparseable cookie holds no revocable token — clear and
            // treat the call as the idempotent no-op it is.
            RefreshCookieBinder.Clear(ctx);
            return Results.NoContent();
        }

        var record = await refreshTokenStore.FindByHashAsync(tokenHash, ct);
        var user = record is null
            ? null
            : await userManager.FindByIdAsync(record.UserId.ToString());

        await refreshTokenStore.RevokeAsync(tokenHash, ct);

        // Audited only when the presented token resolves to a row: an
        // unauthenticated caller with a garbage cookie must not be able to
        // mint audit rows (the revoke itself is a harmless no-op).
        if (record is not null)
        {
            await auditLogger.LogAsync(
                new AuditEvent(
                    ActorUserId: user?.Id,
                    ActorRole: user?.Role.ToString(),
                    Action: AuditActions.Logout,
                    TargetType: "user",
                    TargetId: user?.Id.ToString(),
                    MetadataJson: JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        ["refresh_token_id"] = record.Id.ToString(),
                    }),
                    Ip: ctx.Connection.RemoteIpAddress),
                ct);
        }

        RefreshCookieBinder.Clear(ctx);
        return Results.NoContent();
    }

    /// <summary>
    /// Lookup key for a presented refresh cookie. M1T4's store persists
    /// <c>SHA-256</c> of the 32 raw token bytes (SQ-9 / Q-A10) while the
    /// cookie carries their base64 form — so the raw bytes are recovered by
    /// decoding before hashing (the task step 8 snippet hashes the base64
    /// text, which can never match the store; the store's semantics are the
    /// authoritative contract). A malformed value returns <c>null</c>.
    /// </summary>
    private static byte[]? RefreshTokenHash(string rawToken)
    {
        try
        {
            return SHA256.HashData(Convert.FromBase64String(rawToken));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static IResult OtpProblem(HttpContext ctx, string detail)
        => ProblemDetailsHelpers.Problem(
            ctx,
            StatusCodes.Status401Unauthorized,
            ErrorTypes.Unauthorized,
            "Unauthorized",
            detail);

    private static IResult BodyRequired(HttpContext ctx)
        => ProblemDetailsHelpers.Problem(
            ctx,
            StatusCodes.Status400BadRequest,
            ErrorTypes.Validation,
            "Validation failed",
            "A JSON request body is required.",
            new[] { ("body", "A JSON request body is required.") });

    /// <summary>400 for a login/otp-login request whose body is not form-encoded.</summary>
    private static IResult FormBodyRequired(HttpContext ctx, string expectedFields)
        => ProblemDetailsHelpers.Problem(
            ctx,
            StatusCodes.Status400BadRequest,
            ErrorTypes.Validation,
            "Validation failed",
            "Expected an application/x-www-form-urlencoded body.",
            new[] { ("body", $"A form body with {expectedFields} is required.") });

    /// <summary><c>metadata_json</c> for a login blocked by a Disabled status.</summary>
    private static string DisabledAccountMetadata(string email)
        => JsonSerializer.Serialize(
            new Dictionary<string, string>
            {
                ["email"] = email.Trim(),
                ["reason"] = "account_disabled",
            });

    private static IResult ValidationProblem(
        HttpContext ctx,
        FluentValidation.Results.ValidationResult validation)
        => ProblemDetailsHelpers.Problem(
            ctx,
            StatusCodes.Status400BadRequest,
            ErrorTypes.Validation,
            "Validation failed",
            "One or more fields are invalid.",
            validation.Errors
                .Select(error => (Name: WireFieldName(error.PropertyName), Reason: error.ErrorMessage))
                .ToList());

    /// <summary>
    /// CLR property name → the §13.12 <c>name</c> the client actually sent:
    /// the §13.13/§13.3 wire fields are snake_case, so validation failures
    /// name <c>passphrase_confirm</c>, not <c>PassphraseConfirm</c>.
    /// Unknown names (e.g. the <c>body</c> sentinel) pass through.
    /// </summary>
    private static string WireFieldName(string propertyName)
        => propertyName switch
        {
            nameof(SetPassphraseRequest.Passphrase) => "passphrase",
            nameof(SetPassphraseRequest.PassphraseConfirm) => "passphrase_confirm",
            nameof(ChangePasswordRequest.OldPassword) => "old_password",
            nameof(ChangePasswordRequest.NewPassword) => "new_password",
            nameof(ChangePasswordRequest.NewPasswordConfirm) => "new_password_confirm",
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

    /// <summary><c>metadata_json</c> for auth events — the email lives here, never in actor (AC).</summary>
    private static string EmailMetadata(string email)
        => JsonSerializer.Serialize(
            new Dictionary<string, string> { ["email"] = email.Trim() });

    private static readonly JsonSerializerOptions BodySerializerOptions =
        new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads the raw request body and deserializes it as
    /// <typeparamref name="TRequest"/>. An absent or malformed payload comes
    /// back as a ready-made §13.12 400 problem (<c>Error</c>) so the
    /// AC "every 4xx is <c>application/problem+json</c>" holds even for
    /// malformed JSON — the framework's minimal-API binder would otherwise
    /// answer with an empty-bodied 400. Exactly one tuple element is set:
    /// non-null <c>Body</c> paired with null <c>Error</c>, or the reverse.
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
