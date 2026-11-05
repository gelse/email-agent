using System.Net;

namespace EmailAgent.Spa.Auth;

/// <summary>
/// M1T8: the <c>Api</c> client's <see cref="DelegatingHandler"/> — attaches
/// <c>Authorization: Bearer</c> from the <see cref="TokenStore"/> to every
/// outbound request (when a token exists) and, on a 401, attempts <b>one</b>
/// silent refresh (<see cref="RefreshCookieAuthenticator"/>, which posts the
/// httpOnly <c>ea_refresh</c> cookie through the same-origin refresh path)
/// before retrying the original request once with the fresh token. The retry
/// never re-enters the refresh branch, so a persistent 401 cannot loop.
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
