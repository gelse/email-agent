using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using EmailAgent.Core.Options;
using EmailAgent.Infrastructure.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace EmailAgent.Api.Auth;

/// <summary>Issues HS256 JWT access tokens (M1T4 task step 5).</summary>
public interface IJwtTokenIssuer
{
    /// <summary>
    /// Returns a signed compact JWT with <c>sub</c> (user id), one
    /// <c>role</c> claim per role, <c>jti</c>, <c>iat</c>, <c>iss</c>,
    /// <c>aud</c> and <c>exp</c> = now + <c>AUTH__AccessTokenTtl</c>.
    /// </summary>
    string IssueAccessToken(User user, IEnumerable<string> roles);
}

/// <summary>Default <see cref="IJwtTokenIssuer"/> (M1T4 task step 5).</summary>
public sealed class JwtTokenIssuer(IOptions<AuthOptions> options) : IJwtTokenIssuer
{

    public string IssueAccessToken(User user, IEnumerable<string> roles)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(roles);

        if (string.IsNullOrWhiteSpace(options.Value.SigningKey)
            || Encoding.UTF8.GetByteCount(options.Value.SigningKey) < 32)
        {
            throw new InvalidOperationException(
                "Auth:SigningKey (env AUTH__SigningKey) must be present and at least 32 bytes (§16).");
        }

        var now = DateTimeOffset.UtcNow;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
        };

        foreach (var role in roles)
        {
            claims.Add(new Claim("role", role));
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Value.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: options.Value.Issuer,
            audience: options.Value.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: (now + options.Value.AccessTokenTtl).UtcDateTime,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
