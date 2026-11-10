using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace EmailAgent.Api.Auth;

/// <summary>
/// <c>Origin</c> validation on the refresh path (M1T4 task step 8, §13.9 /
/// §14): the request must either carry no <c>Origin</c> (non-browser client,
/// e.g. curl/SPA server-side fetch) or an origin that is in the allow-list.
/// </summary>
/// <remarks>
/// <para>
/// Allow-list composition (§16): when <c>SPA__AllowedOrigins</c> is non-empty
/// those origins are allowed; <c>API__PublicBaseUrl</c>'s origin is always
/// allowed (it is the canonical same-origin default). Independently of the
/// list, the request's own origin (<c>{scheme}://{host}</c>) is always
/// accepted — with both settings empty (the shipped default) that is the
/// only accepted browser origin, realizing the documented "empty by default
/// → accept same-origin only" posture. An external origin such as
/// <c>https://evil.example</c> is rejected with 403 — see the M1T4
/// acceptance criterion.
/// </para>
/// </remarks>
public sealed class OriginValidator(IConfiguration configuration)
{

    /// <summary>
    /// Returns <c>true</c> when the request's <c>Origin</c> header is absent
    /// or matches the configured allow-list; <c>false</c> otherwise.
    /// </summary>
    public bool IsAllowed(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Request.Headers.TryGetValue("Origin", out var originValues)
            || StringValues.IsNullOrEmpty(originValues))
        {
            return true; // non-browser client
        }

        var origin = originValues.ToString().Trim();
        if (origin.Length == 0 || origin == "null")
        {
            return false;
        }

        // Same-origin always passes: with the default (empty) allow-list this
        // is the only accepted browser origin — "empty by default → accept
        // same-origin only" (task scope, Origin validation on the refresh path).
        if (IsSameOrigin(context, origin))
        {
            return true;
        }

        return IsAllowedOrigin(origin);
    }

    /// <summary>True when <paramref name="origin"/> equals this request's own origin.</summary>
    private static bool IsSameOrigin(HttpContext context, string origin)
    {
        var self = Uri.TryCreate(
            $"{context.Request.Scheme}://{context.Request.Host}",
            UriKind.Absolute,
            out var selfUri)
            ? selfUri.GetLeftPart(UriPartial.Authority)
            : string.Empty;

        return self.Length > 0
            && string.Equals(origin, self, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="origin"/> is on the allow-list.</summary>
    public bool IsAllowedOrigin(string origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return false;
        }

        foreach (var allowed in EnumerateAllowedOrigins())
        {
            if (string.Equals(allowed, origin, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private IEnumerable<string> EnumerateAllowedOrigins()
    {
        var csv = configuration["Spa:AllowedOrigins"]
            ?? configuration["Spa__AllowedOrigins"]
            ?? string.Empty;

        foreach (var part in csv.Split(
            new[] { ',', ';', ' ' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Normalize(part);
        }

        var baseUrl = configuration["Api:PublicBaseUrl"]
            ?? configuration["Api__PublicBaseUrl"]
            ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            yield return uri.GetLeftPart(UriPartial.Authority);
        }
    }

    private static string Normalize(string origin)
    {
        var trimmed = origin.TrimEnd('/');
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority)
            : trimmed;
    }

    /// <summary>RFC 7807 body written on a rejected Origin (task step 8).</summary>
    public static Task WriteForbiddenAsync(HttpContext context, CancellationToken ct = default)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/problem+json";
        return context.Response.WriteAsync(
            JsonSerializer.Serialize(new
            {
                type = "/errors/forbidden",
                title = "Forbidden",
                status = StatusCodes.Status403Forbidden,
                detail = "The request Origin is not allowed on the refresh path.",
                instance = context.Request.Path.Value,
            }),
            ct);
    }
}
