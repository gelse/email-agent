using Microsoft.AspNetCore.Http;

namespace EmailAgent.Api.Auth;

/// <summary>
/// M1T6 task step 2: read / write / clear the <c>ea_refresh</c> cookie for
/// the account endpoints. The wire format (name, <c>Path=/v1/account/refresh</c>,
/// HttpOnly, Secure, SameSite=Lax) stays owned by M1T4's
/// <see cref="RefreshTokenCookieWriter"/> — this binder only pairs the read
/// side with the write/clear side so the endpoints have one façade.
/// </summary>
/// <remarks>
/// <c>Secure</c> is always on (production terminates TLS per §17). The dev
/// compose stack serves plain HTTP, so a browser drops the cookie there —
/// the task file's <c>IsDevelopment</c> mitigation is a known, documented
/// risk deferred to M1T8/M1T9 (changing M1T4's writer would break its
/// pinned cookie-attribute assertions).
/// </remarks>
public static class RefreshCookieBinder
{
    /// <summary>Returns the raw <c>ea_refresh</c> cookie value, or <c>null</c>.</summary>
    public static string? ReadRaw(HttpContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        return ctx.Request.Cookies.TryGetValue(RefreshTokenCookieWriter.CookieName, out var raw)
            && !string.IsNullOrEmpty(raw)
                ? raw
                : null;
    }

    /// <summary>Sets the refresh cookie with M1T4's pinned attributes.</summary>
    public static void Write(HttpContext ctx, string rawToken, DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        RefreshTokenCookieWriter.Write(ctx.Response, rawToken, expiresAt);
    }

    /// <summary>Clears the refresh cookie (<c>Max-Age = 0</c>).</summary>
    public static void Clear(HttpContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        RefreshTokenCookieWriter.Clear(ctx.Response);
    }
}
