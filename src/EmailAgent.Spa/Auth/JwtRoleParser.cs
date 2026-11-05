using System.IdentityModel.Tokens.Jwt;

namespace EmailAgent.Spa.Auth;

/// <summary>
/// M1T8: extracts claims from the access token for SPA routing only. The JWT
/// is parsed but <b>not</b> verified cryptographically — verification is the
/// API's job on every request; the SPA reads <c>role</c> (and display claims)
/// purely to choose which route to render (task Risks: "Role parsing is
/// unsigned").
/// </summary>
public sealed class JwtRoleParser
{
    /// <summary>Returns every <c>role</c> claim in <paramref name="jwt"/>; empty when unparseable.</summary>
    public IReadOnlyList<string> GetRoles(string jwt)
        => ReadClaims(jwt, "role");

    /// <summary>Returns the first <c>email</c> claim, or <c>null</c>.</summary>
    public string? GetEmail(string jwt)
        => ReadClaims(jwt, "email").FirstOrDefault();

    /// <summary>Returns the <c>sub</c> claim (user id), or <c>null</c>.</summary>
    public string? GetSubject(string jwt)
        => ReadClaims(jwt, "sub").FirstOrDefault();

    /// <summary>
    /// Parses the compact JWT with <see cref="JwtSecurityToken(string)"/>,
    /// which decodes without validating the signature. Malformed input
    /// yields no claims rather than an exception (routing must degrade to
    /// "not authorized", never crash).
    /// </summary>
    private static IReadOnlyList<string> ReadClaims(string jwt, string claimType)
    {
        if (string.IsNullOrWhiteSpace(jwt))
        {
            return Array.Empty<string>();
        }

        try
        {
            var token = new JwtSecurityToken(jwt);
            return token.Claims
                .Where(claim => claim.Type == claimType)
                .Select(claim => claim.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
        }
        catch (ArgumentException)
        {
            // Malformed header/payload/base64 — treated as "no claims".
            return Array.Empty<string>();
        }
    }
}
