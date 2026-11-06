using System.Net;

namespace EmailAgent.Spa.Auth;

/// <summary>
/// M1T8: the <c>Api</c> client's <see cref="DelegatingHandler"/> — attaches
/// <c>Authorization: Bearer</c> from the <see cref="TokenStore"/> to every
/// outbound request (when a token exists) and, on a 401, attempts <b>one</b>
/// silent refresh (<see cref="RefreshCookieAuthenticator"/>, which posts the
/// httpOnly <c>ea_refresh</c> cookie through the same-origin refresh path)
/// before retrying the original request once with the fresh token. The retry
/// never re-enters the refresh branch, so a persistent 401 cannot loop, and a
/// 401 that is a verdict on a credential the caller just sent (the M2T6
/// unwrap-dek passphrase, a login password) is never refreshed or retried —
/// while a JwtBearer challenge for an expired token still is.
/// </summary>
public sealed class BearerTokenHandler : DelegatingHandler
{
    private readonly TokenStore _tokenStore;
    private readonly RefreshCookieAuthenticator _authenticator;

    public BearerTokenHandler(TokenStore tokenStore, RefreshCookieAuthenticator authenticator)
    {
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await SendCoreAsync(request, cancellationToken, allowRefreshRetry: true)
            .ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpRequestMessage request, CancellationToken ct, bool allowRefreshRetry)
    {
        AttachBearer(request);

        var response = await base.SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Unauthorized
            || !allowRefreshRetry
            || _tokenStore.AccessToken is null)
        {
            return response;
        }

        // M2T6 review fix (WARNING, round 1): a 401 from a credential-checking
        // endpoint means "that secret is wrong", not "this token is stale".
        // Refreshing and re-sending would run the expensive Argon2id unwrap
        // twice, write two dek_unwrap_failed audit rows for one typo, and
        // rotate the refresh cookie on a failed passphrase guess.
        //
        // Round 2 refined this: the *route* alone cannot decide. An expired
        // access token reaching the same route produces a JwtBearer
        // **challenge** — Program.cs's OnChallenge sets `WWW-Authenticate:
        // Bearer`, which no endpoint-authored problem document carries — and
        // that 401 genuinely is "stale token": refreshing it lets a correct
        // passphrase succeed instead of being misreported as wrong.
        if (IsCredentialVerdict(request) && !IsTokenChallenge(response))
        {
            return response;
        }

        // Stale/expired access token: one silent refresh (cookie flow), then
        // retry this request exactly once with the new token. A 401 on the
        // retried copy is returned as-is (no refresh loop).
        if (!await _authenticator.AttemptSilentRefreshAsync(ct).ConfigureAwait(false))
        {
            return response;
        }

        response.Dispose();
        var retry = await CloneAsync(request, ct).ConfigureAwait(false);
        AttachBearer(retry);
        return await SendCoreAsync(retry, ct, allowRefreshRetry: false).ConfigureAwait(false);
    }

    private void AttachBearer(HttpRequestMessage request)
    {
        var token = _tokenStore.AccessToken;
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
    }

    /// <summary>
    /// True for the routes whose 401 may be a <b>verdict on a credential the
    /// caller just supplied</b> (a passphrase or a password) rather than on
    /// the access token. Those must be surfaced to the user exactly once: a
    /// refresh-retry would re-post the same secret (two Argon2id unwraps, two
    /// audit rows, one needless cookie rotation) for a result that is already
    /// final. See <see cref="SendCoreAsync"/>: the response still decides —
    /// a JwtBearer challenge on the same route means the token, not the
    /// secret, is the problem.
    /// </summary>
    private static bool IsCredentialVerdict(HttpRequestMessage request)
    {
        var path = request.RequestUri?.AbsolutePath;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return path.EndsWith("/unwrap-dek", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/v1/account/login", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/v1/account/otp-login", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the 401 is an authentication <b>challenge</b> (missing,
    /// expired or otherwise rejected access token): the API's JwtBearer
    /// <c>OnChallenge</c> stamps <c>WWW-Authenticate: Bearer</c> before it
    /// writes its problem document, while an endpoint's own 401 (wrong
    /// passphrase, dead account) never sets the header.
    /// </summary>
    private static bool IsTokenChallenge(HttpResponseMessage response)
        => response.Headers.Contains("WWW-Authenticate");

    /// <summary>
    /// Deep-copies a request so it can be sent again (method, headers,
    /// buffered content and options). <see cref="HttpContent"/> is buffered
    /// into memory first — form/JSON bodies sent by the SPA pages are small.
    /// </summary>
    private static async Task<HttpRequestMessage> CloneAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in request.Options)
        {
            clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        }

        if (request.Content is not null)
        {
            var buffer = new MemoryStream();
            await request.Content.CopyToAsync(buffer, ct).ConfigureAwait(false);
            buffer.Position = 0;
            clone.Content = new StreamContent(buffer);
            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }
}
