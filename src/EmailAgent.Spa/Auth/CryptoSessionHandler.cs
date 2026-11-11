namespace EmailAgent.Spa.Auth;

/// <summary>
/// M5T1: the <c>Api</c> client's <see cref="DelegatingHandler"/> for the
/// session-key envelope (§13.17 / SQ-M5-1) — attaches
/// <c>X-Crypto-Session</c> from the <see cref="TokenStore"/>'s
/// <see cref="CryptoSessionStore"/> to the requests that read it (every
/// route under <c>/v1/user/*</c>, which <c>CryptoSessionMiddleware</c>
/// guards generically), strips a stale value everywhere else, and sends no
/// such header once the session is locked or logged out. Replaces M2T6's
/// <c>DekHeaderHandler</c> (this task's removal point for <c>X-User-Dek</c>).
/// </summary>
/// <remarks>
/// The handler sits inside <see cref="BearerTokenHandler"/> (registered after
/// it), so the 401-retry clone is re-stamped with the crypto-session token
/// instead of losing it, and the header is never logged or persisted here —
/// it is just read from memory and placed on the request. The header rides
/// only the <c>/v1/user/*</c> namespace that enforces it (the M2T6
/// blast-radius precedent): login, refresh, unlock, admin and health calls
/// carry no session credential they would not be judged on.
/// </remarks>
public sealed class CryptoSessionHandler(TokenStore tokenStore) : DelegatingHandler
{
    /// <summary>The session-key envelope header every user-data route reads (server: <c>HttpCryptoSessionAccessor.HeaderName</c>).</summary>
    public const string HeaderName = "X-Crypto-Session";

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var token = tokenStore.CryptoSession.Token;

        // Stale copy first: a retry clone can arrive carrying one, and two
        // values would make the server read the wrong session.
        request.Headers.Remove(HeaderName);

        if (NeedsSession(request) && !string.IsNullOrWhiteSpace(token))
        {
            request.Headers.TryAddWithoutValidation(HeaderName, token);
        }

        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>True for the routes <c>CryptoSessionMiddleware</c> enforces (the whole <c>/v1/user/*</c> namespace).</summary>
    internal static bool NeedsSession(HttpRequestMessage request)
    {
        var path = request.RequestUri?.AbsolutePath;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return path.Equals("/v1/user", StringComparison.Ordinal)
            || path.StartsWith("/v1/user/", StringComparison.Ordinal);
    }
}
