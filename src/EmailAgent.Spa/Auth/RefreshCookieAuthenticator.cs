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

            _tokenStore.Set(body.AccessToken);
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
