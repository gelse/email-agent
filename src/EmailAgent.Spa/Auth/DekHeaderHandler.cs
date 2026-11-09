namespace EmailAgent.Spa.Auth;

/// <summary>
/// M2T6: the <c>Api</c> client's second <see cref="DelegatingHandler"/> —
/// attaches <c>X-User-Dek</c> from the <see cref="TokenStore"/>'s
/// <see cref="DekStore"/> to the requests that read it (the
/// <c>/v1/user/sources</c> namespace) while a key is cached, strips a stale
/// value everywhere else, and sends no such header once the session is locked
/// or logged out.
/// </summary>
/// <remarks>
/// M2-only transport (SQ-12 / §13.16): the header is how the M2 sources
/// endpoints get the caller's data-encryption key, because M1/M2 have no
/// session-key envelope. M5T1's envelope replaces it. The handler sits
/// inside <see cref="BearerTokenHandler"/> (registered after it), so the
/// 401-retry clone is re-stamped with the DEK instead of losing it, and the
/// header is never logged or persisted here — it is just read from memory
/// and placed on the request.
/// </remarks>
public sealed class DekHeaderHandler : DelegatingHandler
{
    /// <summary>The M2-only header the <c>/v1/user/sources</c> namespace reads (server: <c>SourcesHandlers.DekHeaderName</c>).</summary>
    public const string HeaderName = "X-User-Dek";

    private readonly TokenStore _tokenStore;

    public DekHeaderHandler(TokenStore tokenStore)
    {
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var dek = _tokenStore.DekStore.DekBase64;

        // Stale copy first: a retry clone can arrive carrying one, and two
        // values would make the server read the wrong key.
        request.Headers.Remove(HeaderName);

        // M2T6 review fix: only the namespace that reads the header gets it.
        // Shipping the key on login, refresh, unwrap or admin calls widens the
        // blast radius of a mis-cached key without benefit — the server-side
        // sources handlers are the sole consumers (SourcesHandlers.DekHeaderName).
        if (NeedsDek(request) && !string.IsNullOrWhiteSpace(dek))
        {
            request.Headers.TryAddWithoutValidation(HeaderName, dek);
        }

        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>True for the routes that actually read <c>X-User-Dek</c>.</summary>
    internal static bool NeedsDek(HttpRequestMessage request)
    {
        var path = request.RequestUri?.AbsolutePath;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return path.Equals("/v1/user/sources", StringComparison.Ordinal)
            || path.StartsWith("/v1/user/sources/", StringComparison.Ordinal)
            // M4T3/T4: action-config read/write endpoints (list decrypts; CRUD uses DEK).
            || path.Equals("/v1/user/actions", StringComparison.Ordinal)
            || path.StartsWith("/v1/user/actions/", StringComparison.Ordinal);
    }
}
