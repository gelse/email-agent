using System.Net;
using System.Net.Http.Json;
using System.Text;
using EmailAgent.Spa.Auth;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Spa;

/// <summary>
/// M2T6 review fix (WARNING): <see cref="BearerTokenHandler"/> refreshes and
/// retries on a 401 only when the 401 means "this access token is stale". The
/// unwrap endpoint answers 401 for a <b>wrong passphrase</b>
/// (<c>/errors/credential-mismatch</c>), and refreshing there would post the
/// same passphrase twice — two Argon2id derivations, two
/// <c>dek_unwrap_failed</c> audit rows, one needless refresh-cookie rotation
/// (which can trip the reuse detector across tabs). The positive control pins
/// that a genuinely stale token still refreshes exactly once and retries
/// exactly once.
/// </summary>
public class BearerTokenHandlerTests
{
    [Fact]
    public async Task Credential401_IsNeitherRefreshedNorRetried()
    {
        var store = new TokenStore();
        store.Set(SpaTestContext.FakeJwt(role: "User"));

        var refresh = new CountingHandler(_ => Ok("{\"access_token\":\"refreshed-token\",\"expires_in\":900}"));
        var api = new CountingHandler(_ => Problem(
            HttpStatusCode.Unauthorized,
            "/errors/credential-mismatch",
            "The passphrase is not valid."));

        using var client = Client(store, refresh, api);

        using var response = await client.PostAsJsonAsync(
            "/v1/user/account/unwrap-dek", new { passphrase = "wrong" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        api.Calls.Should().Be(1, "a wrong passphrase must be attempted exactly once");
        refresh.Calls.Should().Be(0, "no refresh-cookie rotation for a credential verdict");
        store.AccessToken.Should().Be(SpaTestContext.FakeJwt(role: "User"),
            "the still-valid access token is untouched");
    }

    [Fact]
    public async Task StaleToken401_RefreshesOnce_AndRetriesOnce()
    {
        var store = new TokenStore();
        store.Set(SpaTestContext.FakeJwt(role: "User"));

        var refresh = new CountingHandler(_ => Ok("{\"access_token\":\"refreshed-token\",\"expires_in\":900}"));
        var api = new CountingHandler(_ => Problem(
            HttpStatusCode.Unauthorized, "/errors/unauthorized", "expired"));

        using var client = Client(store, refresh, api);

        using var response = await client.GetAsync("/v1/user/sources");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "still 401 after the retry");
        refresh.Calls.Should().Be(1, "exactly one silent refresh for a stale token");
        api.Calls.Should().Be(2, "the original attempt plus one retry");
    }

    /// <summary>
    /// M2T6 review fix (round 2): the credential-verdict bypass keys on the
    /// *answer*, not just the route — an expired access token reaching the
    /// unwrap endpoint answers with a JwtBearer challenge
    /// (<c>WWW-Authenticate: Bearer</c>), and swallowing THAT would report a
    /// correct passphrase as invalid with no way out.
    /// </summary>
    [Fact]
    public async Task ExpiredTokenChallenge_OnUnwrapRoute_StillRefreshesAndRetries()
    {
        var store = new TokenStore();
        store.Set(SpaTestContext.FakeJwt(role: "User"));

        var refresh = new CountingHandler(_ => Ok("{\"access_token\":\"refreshed-token\",\"expires_in\":900}"));
        var api = new CountingHandler(request =>
            request.Headers.Authorization?.Parameter == "refreshed-token"
                ? Ok("{\"dek\":\"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=\"}")
                : Challenge("expired"));

        using var client = Client(store, refresh, api);

        using var response = await client.PostAsJsonAsync(
            "/v1/user/account/unwrap-dek", new { passphrase = "right" });

        response.IsSuccessStatusCode.Should().BeTrue(
            "the retry with the refreshed token carries the correct passphrase through");
        refresh.Calls.Should().Be(1, "a token challenge is a stale-token 401, not a credential verdict");
        api.Calls.Should().Be(2);
    }

    [Fact]
    public async Task SuccessfulRefresh_RetriesWithTheFreshToken()
    {
        var store = new TokenStore();
        store.Set(SpaTestContext.FakeJwt(role: "User"));

        var refresh = new CountingHandler(_ => Ok("{\"access_token\":\"refreshed-token\",\"expires_in\":900}"));
        var api = new CountingHandler(request =>
            request.Headers.Authorization?.Parameter == "refreshed-token"
                ? Ok("{\"items\":[],\"next_cursor\":null}")
                : Problem(HttpStatusCode.Unauthorized, "/errors/unauthorized", "expired"));

        using var client = Client(store, refresh, api);

        using var response = await client.GetAsync("/v1/user/sources");

        response.IsSuccessStatusCode.Should().BeTrue("the retry carries the refreshed token");
        api.Calls.Should().Be(2);
        store.AccessToken.Should().Be("refreshed-token");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static HttpClient Client(
        TokenStore store, CountingHandler refresh, CountingHandler api)
        => new(new BearerTokenHandler(store, new RefreshCookieAuthenticator(
            new FixedClientFactory(refresh), store))
        {
            InnerHandler = api,
        })
        {
            BaseAddress = new Uri("http://localhost/"),
        };

    private static HttpResponseMessage Ok(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Problem(HttpStatusCode status, string type, string detail)
        => new(status)
        {
            Content = new StringContent(
                $"{{\"type\":\"{type}\",\"title\":\"Unauthorized\",\"status\":{(int)status},\"detail\":\"{detail}\"}}",
                Encoding.UTF8,
                "application/problem+json"),
        };

    /// <summary>The API's JwtBearer OnChallenge shape: 401 + <c>WWW-Authenticate: Bearer</c> + problem body.</summary>
    private static HttpResponseMessage Challenge(string detail)
    {
        var response = Problem(HttpStatusCode.Unauthorized, "/errors/unauthorized", detail);
        response.Headers.WwwAuthenticate.TryParseAdd("Bearer");
        return response;
    }

    /// <summary>Answers through the supplied function and counts every request it serves.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_responder(request));
        }
    }

    /// <summary>One named client over the counting handler (Program.cs's "RefreshClient" stand-in).</summary>
    private sealed class FixedClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public FixedClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name)
            => new(_handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost/"),
            };
    }
}
