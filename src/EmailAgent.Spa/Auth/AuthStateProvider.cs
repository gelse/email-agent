using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace EmailAgent.Spa.Auth;

/// <summary>
/// M1T8 (task step 6): <see cref="AuthenticationStateProvider"/> over the
/// in-memory <see cref="TokenStore"/>. No API round-trip: with no token the
/// caller is anonymous; with a token the claims (<c>sub</c>, <c>role</c>,
/// <c>email</c>) are parsed straight from the JWT by
/// <see cref="JwtRoleParser"/>. Every token-store change re-issues the auth
/// state so <c>AuthorizeRouteView</c>/<c>AuthorizeView</c> re-evaluate.
/// </summary>
public sealed class AuthStateProvider : AuthenticationStateProvider, IDisposable
{
    private readonly TokenStore _tokenStore;
    private readonly JwtRoleParser _roleParser;
    private bool _disposed;

    public AuthStateProvider(TokenStore tokenStore, JwtRoleParser roleParser)
    {
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _roleParser = roleParser ?? throw new ArgumentNullException(nameof(roleParser));
        _tokenStore.Changed += OnTokenChanged;
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var token = _tokenStore.AccessToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity()))); // anonymous
        }

        var identity = new ClaimsIdentity(
            BuildClaims(token),
            authenticationType: "jwt"); // non-empty type ⇒ IsAuthenticated == true

        return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
    }

    private IEnumerable<Claim> BuildClaims(string token)
    {
        var subject = _roleParser.GetSubject(token);
        if (!string.IsNullOrWhiteSpace(subject))
        {
            yield return new Claim(ClaimTypes.NameIdentifier, subject);
        }

        var email = _roleParser.GetEmail(token) ?? _tokenStore.SessionEmail;
        if (!string.IsNullOrWhiteSpace(email))
        {
            yield return new Claim(ClaimTypes.Email, email);
            yield return new Claim(ClaimTypes.Name, email);
        }

        foreach (var role in _roleParser.GetRoles(token))
        {
            yield return new Claim(ClaimTypes.Role, role);
        }
    }

    private void OnTokenChanged()
        => NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _tokenStore.Changed -= OnTokenChanged;
        _disposed = true;
    }
}
