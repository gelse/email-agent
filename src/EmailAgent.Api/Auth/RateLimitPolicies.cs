using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;

namespace EmailAgent.Api.Auth;

/// <summary>
/// Q-O7 rate-limit envelopes: 5/min per IP and per account on the login
/// endpoints, 60/min per authenticated user, plus the §13.14 header emission
/// rules (M1T4 task step 7).
/// </summary>
/// <remarks>
/// <para>
/// The three partitions are named policies registered by
/// <see cref="AddRateLimitPolicies"/>. The authenticated budget +
/// <c>X-RateLimit-*</c> success-path headers are emitted by
/// <see cref="AuthenticatedRateLimitMiddleware"/> (the built-in limiter has
/// no success-path lease metadata). §13.14: "Unauthenticated responses do
/// not carry these headers." The two login partitions emit only
/// <c>Retry-After</c>, on 429.
/// </para>
/// <para>
/// Q-O7's arithmetic is pinned here as constants and exercised by the
/// integration suite: the login endpoints must accept exactly five
/// attempts per window (the sixth returns 429); the per-user API budget
/// is 60 per minute.
/// </para>
/// </remarks>
public static class RateLimitPolicies
{
    public const string LoginPerIp = "LoginPerIp";
    public const string LoginPerAccount = "LoginPerAccount";
    public const string AuthenticatedPerUser = "AuthenticatedPerUser";

    /// <summary>Q-O7: login attempts per minute, per IP and per account.</summary>
    public const int LoginPerMinute = 5;

    /// <summary>Q-O7: authenticated API requests per minute, per user.</summary>
    public const int AuthenticatedPerMinute = 60;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The first rejected login index (0-based): the sixth attempt, i.e.
    /// <see cref="LoginPerMinute"/> successes are allowed per window.
    /// </summary>
    public static int FirstRejectedLoginIndex => LoginPerMinute;

    /// <summary>
    /// Unix-seconds window reset for the fixed window that started at
    /// <paramref name="windowStartUtc"/> (§13.14 <c>X-RateLimit-Reset</c>).
    /// </summary>
    public static long ResetUnixSeconds(DateTimeOffset windowStartUtc)
        => (windowStartUtc + Window).ToUnixTimeSeconds();

    /// <summary>
    /// Registers the three named policies, the Q-O7 per-IP login budget on
    /// <see cref="RateLimiterOptions.GlobalLimiter"/>, and the
    /// <c>OnRejected</c> callback that writes <c>Retry-After</c> plus the
    /// RFC 7807 body (task step 7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// M1T6 wiring (task steps 4 + 10): the framework's rate-limiting
    /// middleware honors exactly <b>one</b> <c>EnableRateLimiting</c> policy
    /// per endpoint (metadata lookup yields a single match), so the two Q-O7
    /// login budgets cannot be stacked as two attributes on one route. The
    /// per-account budget is the endpoint policy
    /// (<see cref="LoginPerAccount"/> — its partition key is the request's
    /// <c>email</c> form value, primed asynchronously by Program.cs before
    /// <c>UseRateLimiter</c> so the synchronous partition factory never reads
    /// an unbuffered body), and the per-IP budget rides the global limiter,
    /// which this method scopes to the two login paths. A request rejected by
    /// either budget takes the same <c>OnRejected</c> writer below, so the
    /// 429 response shape is identical whichever half trips first.
    /// </para>
    /// <para>
    /// When a login request passes the IP budget but fails the account
    /// budget, the IP permit is still consumed (it arrived from that IP) —
    /// exactly the "5 per IP <b>and</b> per account" arithmetic of Q-O7.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddRateLimitPolicies(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Q-O7 per-IP half of the login budget: 5/min per remote address
            // on /v1/account/login and /v1/account/otp-login only. Non-login
            // paths get the no-op partition (a single cached "n/a" entry), so
            // the health, metrics and account endpoints outside login are
            // metered only by their own endpoint policy / the authenticated
            // middleware below.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                httpContext =>
                {
                    if (!IsLoginPath(httpContext.Request.Path))
                    {
                        return RateLimitPartition.GetNoLimiter<string>(NoLimiterPartitionKey);
                    }

                    var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter(
                        $"login-ip:{ip}",
                        _ => CreateFixedWindowLimiter(LoginPerMinute));
                });

            options.AddPolicy(AuthenticatedPerUser, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    ResolveUserKey(httpContext),
                    _ => CreateFixedWindowLimiter(AuthenticatedPerMinute)));

            options.AddPolicy(LoginPerIp, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => CreateFixedWindowLimiter(LoginPerMinute)));

            // M1T6 task step 10: per-account partition keyed on the `email`
            // form value at request time (Q-O7 — a password-spray from one
            // IP across accounts trips the global limiter above; an
            // account-stuffing burst against one account trips this one).
            options.AddPolicy(LoginPerAccount, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    AccountPartitionKey(httpContext),
                    _ => CreateFixedWindowLimiter(LoginPerMinute)));

            options.OnRejected = (context, ct) =>
                WriteRejectionAsync(context, ct);
        });

        return services;
    }

    /// <summary>Shared key of the global limiter's no-op partition (one cache entry).</summary>
    private const string NoLimiterPartitionKey = "-";

    /// <summary>The two Q-O7 login endpoints (URL-segment versioning, §13.7).</summary>
    private static bool IsLoginPath(PathString path)
        => path.StartsWithSegments("/v1/account/login")
            || path.StartsWithSegments("/v1/account/otp-login");

    /// <summary>
    /// Per-account partition key: the lowercased `email` form value when the
    /// request carries one, otherwise a shared fallback bucket. Program.cs
    /// pre-buffers the login forms (async read) before <c>UseRateLimiter</c>,
    /// so the cached <c>Request.Form</c> is served without synchronous body
    /// IO; the catch keeps a mis-ordered pipeline on the safe fallback bucket
    /// instead of failing the request.
    /// </summary>
    private static string AccountPartitionKey(HttpContext httpContext)
    {
        if (!httpContext.Request.HasFormContentType)
        {
            return "login-account:unknown";
        }

        try
        {
            var email = httpContext.Request.Form["email"].ToString().Trim();
            return email.Length == 0
                ? "login-account:unknown"
                : $"login-account:{email.ToLowerInvariant()}";
        }
        catch (InvalidOperationException)
        {
            return "login-account:unknown";
        }
        catch (IOException)
        {
            return "login-account:unknown";
        }
    }

    private static FixedWindowRateLimiterOptions CreateFixedWindowLimiter(int permitLimit) => new()
    {
        PermitLimit = permitLimit,
        Window = Window,
        QueueLimit = 0,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    };

    /// <summary>
    /// The per-user partition key: the JWT <c>sub</c> claim (JwtBearer maps
    /// it onto <see cref="ClaimTypes.NameIdentifier"/>; both spellings are
    /// probed) or a shared anonymous bucket that no authenticated policy
    /// actually meters.
    /// </summary>
    public static string ResolveUserKey(HttpContext httpContext)
        => httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? httpContext.User.FindFirstValue("sub")
            ?? "anonymous";

    /// <summary>
    /// 429 body per §13.12 / task step 7: RFC 7807 plus <c>Retry-After</c>.
    /// <c>X-RateLimit-*</c> is written only for the authenticated partition's
    /// rejection (the login responses are unauthenticated).
    /// </summary>
    private static ValueTask WriteRejectionAsync(
        OnRejectedContext context,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var secondsIntoWindow = (int)(now.ToUnixTimeSeconds() % 60);
        var retryAfterSeconds = Math.Max(1, 60 - secondsIntoWindow);

        context.HttpContext.Response.Headers.RetryAfter =
            retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        // §13.14: unauthenticated responses carry none of the X-RateLimit-*
        // headers; the authenticated partition's rejection is the one place
        // they may appear on a 429.
        if (context.HttpContext.User.Identity?.IsAuthenticated == true)
        {
            context.HttpContext.Response.Headers["X-RateLimit-Limit"] =
                AuthenticatedPerMinute.ToString(CultureInfo.InvariantCulture);
            context.HttpContext.Response.Headers["X-RateLimit-Remaining"] = "0";
            context.HttpContext.Response.Headers["X-RateLimit-Reset"] =
                ResetUnixSeconds(now).ToString(CultureInfo.InvariantCulture);
        }

        context.HttpContext.Response.ContentType = "application/problem+json";
        return new ValueTask(context.HttpContext.Response.WriteAsync(
            JsonSerializer.Serialize(new
            {
                type = "/errors/rate-limited",
                title = "Too Many Requests",
                status = StatusCodes.Status429TooManyRequests,
                detail = "The rate limit for this request has been exceeded.",
                // Path.Value, not Path: PathString has no System.Text.Json
                // converter and would serialize as an object (§13.12 requires
                // `instance` to be the request path string).
                instance = context.HttpContext.Request.Path.Value,
            }),
            ct));
    }
}