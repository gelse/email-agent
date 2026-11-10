using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace EmailAgent.Api.Auth;

/// <summary>
/// §13.14 / Q-O7 / Q-API8 (M1T4): every <b>authenticated</b> response carries
/// <c>X-RateLimit-Limit</c>, <c>X-RateLimit-Remaining</c> and
/// <c>X-RateLimit-Reset</c>, and the 60/min per-user budget is enforced with
/// a real remaining count. The built-in rate limiter exposes no success-path
/// lease metadata, so the authenticated budget + header emission live here;
/// the named login policies (<see cref="RateLimitPolicies"/>) still own the
/// 5/min login 429 path and write only <c>Retry-After</c> for unauthenticated
/// rejections (§13.14: "Unauthenticated responses do not carry these headers").
/// </summary>
public sealed class AuthenticatedRateLimitMiddleware
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly RequestDelegate _next;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, WindowEntry> _counters = new();

    public AuthenticatedRateLimitMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    private readonly record struct WindowEntry(DateTimeOffset WindowStart, int Count);

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await _next(context);
            return;
        }

        var key = RateLimitPolicies.ResolveUserKey(context);
        var now = DateTimeOffset.UtcNow;
        var windowStart = DateTimeOffset.FromUnixTimeSeconds(
            now.ToUnixTimeSeconds() / 60 * 60);
        var limit = RateLimitPolicies.AuthenticatedPerMinute;

        int count;
        lock (_sync)
        {
            if (!_counters.TryGetValue(key, out var entry)
                || entry.WindowStart != windowStart)
            {
                entry = new WindowEntry(windowStart, 0);
            }

            entry = entry with { Count = entry.Count + 1 };
            _counters[key] = entry;
            count = entry.Count;

            if (_counters.Count > 4096)
            {
                foreach (var stale in _counters
                    .Where(kv => kv.Value.WindowStart < windowStart)
                    .Select(kv => kv.Key)
                    .ToList())
                {
                    _counters.Remove(stale);
                }
            }
        }

        var reset = (windowStart + Window).ToUnixTimeSeconds();
        var headers = context.Response.Headers;
        headers["X-RateLimit-Limit"] = limit.ToString(CultureInfo.InvariantCulture);
        headers["X-RateLimit-Remaining"] =
            Math.Max(0, limit - count).ToString(CultureInfo.InvariantCulture);
        headers["X-RateLimit-Reset"] = reset.ToString(CultureInfo.InvariantCulture);

        if (count > limit)
        {
            var retryAfter = Math.Max(1, (int)(windowStart + Window - now).TotalSeconds);
            headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsync(
                JsonSerializer.Serialize(new
                {
                    type = "/errors/rate-limited",
                    title = "Too Many Requests",
                    status = StatusCodes.Status429TooManyRequests,
                    detail = "The rate limit for this request has been exceeded.",
                    // Path.Value, not Path (PathString serializes as an
                    // object without a converter — §13.12 wants a string).
                    instance = context.Request.Path.Value,
                }),
                context.RequestAborted);
            return;
        }

        await _next(context);
    }
}
