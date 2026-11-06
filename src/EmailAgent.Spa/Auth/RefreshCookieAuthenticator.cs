using System.Net.Http.Json;

namespace EmailAgent.Spa.Auth;

/// <summary>
/// M1T8: cookie-based silent authentication (§13.10, task step 4). Issues
/// <c>POST /v1/account/refresh</c> through the cookie-only
/// <c>RefreshClient</c> (no bearer, no handler chain) — the browser attaches
/// the httpOnly <c>ea_refresh</c> cookie automatically because the SPA and
/// the API share one origin in the dev topology (§13.9). On 200 the fresh
/// access token lands in <see cref="TokenStore"/>; on any non-2xx (no cookie,
/// expired, reused, Origin rejected) the call is a no-op and the router falls
/// through to <c>/account/login</c>.
/// </summary>
/// <remarks>
/// Refresh <b>rotates</b> the cookie on every call, so concurrent attempts
/// would present a already-consumed token and trip the reuse detector
/// (family revocation). All refreshes are therefore serialized through a
/// private gate — the WASM dispatcher is single-threaded, but the startup
/// refresh and the bearer retry can still interleave on async continuations.
/// </remarks>
public sealed class RefreshCookieAuthenticator
{
    /// <summary>The cookie-only client name registered in Program.cs.</summary>
    public const string ClientName = "RefreshClient";

    private readonly IHttpClientFactory _clientFactory;
    private readonly TokenStore _tokenStore;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// M2T6 review fix (round 2): claim reader used to decide whether the
    /// refreshed token belongs to the session that is holding a DEK. Stateless
    /// and unverified by design (same rule as routing: the API verifies, the
    /// SPA only compares subjects).
    /// </summary>
    private readonly JwtRoleParser _claims = new();

    public RefreshCookieAuthenticator(IHttpClientFactory clientFactory, TokenStore tokenStore)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
    }

    /// <summary>
    /// Attempts one silent refresh. Returns <c>true</c> when a new access
    /// token was stored; <c>false</c> for every failure mode (the caller
    /// routes to <c>/account/login</c>).
    /// </summary>
    public async Task<bool> AttemptSilentRefreshAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var client = _clientFactory.CreateClient(ClientName);
            using var response = await client
                .PostAsync("/v1/account/refresh", new ByteArrayContent(Array.Empty<byte>()), ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var body = await response.Content
                .ReadFromJsonAsync<TokenResponse>(cancellationToken: ct)
                .ConfigureAwait(false);
            if (body is null || string.IsNullOrWhiteSpace(body.AccessToken))
            {
                return false;
            }

            // The ea_refresh cookie is shared by every tab of the browser: a
            // sign-in as another account elsewhere can make THIS tab's refresh
            // return that account's token. Storing it with the key the current
            // session still caches would pair one user's DEK with another
            // user's requests (a config wrapped under a key nobody can re-open
            // — no re-wrap path exists in M2). A different subject therefore
            // goes through SignIn, which drops the cached key first.
            //
            // M2T6 review fix (round 3): the comparison fails CLOSED. Keeping
            // the key is only allowed when BOTH subjects parsed and matched —
            // an unparseable subject (contract drift, malformed payload) is
            // treated as "not provably the same principal". A false drop costs
            // one passphrase re-prompt; a false keep risks a source wrapped
            // under a foreign key.
            var previous = _tokenStore.AccessToken;
            var previousSubject = previous is null ? null : _claims.GetSubject(previous);
            var nextSubject = _claims.GetSubject(body.AccessToken);
            var samePrincipal = previous is null
                || (previousSubject is not null
                    && nextSubject is not null
                    && string.Equals(previousSubject, nextSubject, StringComparison.Ordinal));

            if (samePrincipal)
            {
                _tokenStore.Set(body.AccessToken);
            }
            else
            {
                _tokenStore.SignIn(body.AccessToken);
            }

            return true;
        }
        catch (HttpRequestException)
        {
            // Network/HTTP failure (offline, proxy error) — treat as "no session".
            return false;
        }
        catch (TaskCanceledException)
        {
            // Timeout or shutdown — treat as "no session"; never crash startup.
            return false;
        }
        catch (System.Text.Json.JsonException)
        {
            // Non-JSON 200 body — treat as "no session".
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }
}
