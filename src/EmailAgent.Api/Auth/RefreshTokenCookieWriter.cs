using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace EmailAgent.Api.Auth;

/// <summary>
/// Writes / clears the rotating refresh cookie (SQ-4 / Q-A7, §13.10):
/// name <c>ea_refresh</c>, <c>Path=/v1/account/refresh</c>, HttpOnly,
/// Secure, SameSite=Lax.
/// </summary>
public static class RefreshTokenCookieWriter
{
    public const string CookieName = "ea_refresh";
    public const string Path = "/v1/account/refresh";

    /// <summary>
    /// Writes the cookie with <c>Expires = expiresAt</c> (typically
    /// <c>issued.Record.ExpiresAt</c>, i.e. now + AUTH__RefreshTokenTtl).
    /// <c>Secure</c> is forced on (the refresh cookie never leaves HTTPS; the
    /// compose front-end terminates TLS per §17).
    /// </summary>
    public static void Write(HttpResponse response, string rawToken, DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrEmpty(rawToken);

        response.Cookies.Append(
            CookieName,
            rawToken,
            new CookieOptions
            {
                Path = Path,
                HttpOnly = true,
                Secure = true,
                SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
                Expires = expiresAt,
            });
    }

    /// <summary>Clears the cookie (<c>Max-Age = 0</c>) on logout.</summary>
    public static void Clear(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        // ResponseCookies.Delete can only stamp an `expires=1970` date; the
        // published contract (M1T6 step 9 / §13.10) specifies `Max-Age=0`, so
        // the clearing Set-Cookie is written explicitly with both spellings
        // (max-age wins in modern browsers; expires covers legacy) plus the
        // cookie's own attributes.
        var cookie = new SetCookieHeaderValue(CookieName, string.Empty)
        {
            Path = Path,
            HttpOnly = true,
            Secure = true,
            SameSite = Microsoft.Net.Http.Headers.SameSiteMode.Lax,
            Expires = DateTimeOffset.UnixEpoch,
            MaxAge = TimeSpan.Zero,
        };
        response.Headers.Append(HeaderNames.SetCookie, cookie.ToString());
    }
}