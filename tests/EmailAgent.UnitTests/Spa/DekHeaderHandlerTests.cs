using System.Net;
using EmailAgent.Spa.Auth;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Spa;

/// <summary>
/// M2T6 coverage for the M2-only <c>X-User-Dek</c> transport handler
/// (SQ-12 / §13.16): a cached key rides every outbound request, a locked or
/// logged-out session sends none, a stale value (e.g. on a 401-retry clone)
/// is removed before the conditional add, and <c>TokenStore.Clear()</c> drops
/// the key together with the access token. A capturing inner handler records
/// the request after this handler stamped it — <c>StubHttpHandler</c> keeps
/// no headers, hence the dedicated seam.
/// </summary>
public class DekHeaderHandlerTests
{
    private const string Dek = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    [Fact]
    public async Task Handler_AttachesXUserDek_WhenDekStored()
    {
        var store = new TokenStore();
        store.SetDek(Dek);
        using var capture = new CapturingHandler();
        using var client = ClientFor(store, capture);

        using var response = await client.GetAsync("/v1/user/sources");

        response.IsSuccessStatusCode.Should().BeTrue();
        capture.Last.Should().NotBeNull();
        capture.Last!.Headers.TryGetValues(DekHeaderHandler.HeaderName, out var values)
            .Should().BeTrue("a cached key is carried on the sources endpoints that read it");
        values!.Should().ContainSingle().Which.Should().Be(Dek);
    }

    /// <summary>
    /// M2T6 review fix: only <c>/v1/user/sources*</c> reads the header, so
    /// login, refresh, unwrap-dek and admin calls never carry the key — a
    /// mis-cached DEK cannot ride a request that has nothing to do with it.
    /// </summary>
    [Theory]
    [InlineData("/v1/account/login")]
    [InlineData("/v1/account/refresh")]
    [InlineData("/v1/user/account/unwrap-dek")]
    [InlineData("/v1/user/account/kdf")]
    [InlineData("/v1/admin/users")]
    public async Task Handler_OmitsHeader_OnRoutesThatDoNotReadIt(string path)
    {
        var store = new TokenStore();
        store.SetDek(Dek);
        using var capture = new CapturingHandler();
        using var client = ClientFor(store, capture);

        using var response = await client.GetAsync(path);

        response.IsSuccessStatusCode.Should().BeTrue();
        capture.Last.Should().NotBeNull();
        capture.Last!.Headers.Contains(DekHeaderHandler.HeaderName).Should().BeFalse(
            $"the key is never attached to {path}");
    }

    /// <summary>
    /// M2T6 review fix (CRITICAL): a fresh sign-in is a new principal — the
    /// previous session's key must not survive into it.
    /// </summary>
    [Fact]
    public void TokenStore_SignIn_DropsCachedDek_ThenStoresToken()
    {
        var store = new TokenStore();
        store.Set(SpaTestContext.FakeJwt(role: "User"));
        store.SetDek(Dek);

        store.SignIn(SpaTestContext.FakeJwt(role: "Admin"));

        store.DekStore.IsEmpty.Should().BeTrue(
            "a sign-in never inherits the DEK the previous account unwrapped");
        store.DekStore.DekBase64.Should().BeNull();
        store.AccessToken.Should().NotBeNull("the new session's token is stored");
        store.AccessToken.Should().NotBe(SpaTestContext.FakeJwt(role: "User"),
            "and it is the new token, not the old one");
    }

    /// <summary>The silent refresh of the SAME session keeps its key (it must not force a re-prompt).</summary>
    [Fact]
    public async Task Handler_KeepsHeader_AcrossSilentRefreshSet()
    {
        var store = new TokenStore();
        store.Set(SpaTestContext.FakeJwt(role: "User"));
        store.SetDek(Dek);
        using var capture = new CapturingHandler();
        using var client = ClientFor(store, capture);

        store.Set(SpaTestContext.FakeJwt(role: "User"));
        using var response = await client.GetAsync("/v1/user/sources");

        response.IsSuccessStatusCode.Should().BeTrue();
        capture.Last!.Headers.GetValues(DekHeaderHandler.HeaderName)
            .Should().ContainSingle("TokenStore.Set (the refresh path) keeps the cached key");
    }

    [Fact]
    public async Task Handler_OmitsHeader_WhenNoDek()
    {
        var store = new TokenStore();
        using var capture = new CapturingHandler();
        using var client = ClientFor(store, capture);

        using var response = await client.GetAsync("/v1/user/sources");

        response.IsSuccessStatusCode.Should().BeTrue();
        capture.Last.Should().NotBeNull();
        capture.Last!.Headers.Contains(DekHeaderHandler.HeaderName).Should().BeFalse(
            "a locked session sends no X-User-Dek at all");
    }

    [Fact]
    public async Task Handler_ReplacesStaleHeader_BeforeSending()
    {
        // The 401-retry path re-enters this handler with a cloned request;
        // two values would let the server read the wrong key, so the stale
        // one is removed first — and dropped entirely when the store is empty.
        var store = new TokenStore();
        using var capture = new CapturingHandler();
        using var client = ClientFor(store, capture);

        using var stale = new HttpRequestMessage(HttpMethod.Get, "/v1/user/sources");
        stale.Headers.TryAddWithoutValidation(DekHeaderHandler.HeaderName, "stale-key-from-a-retry-clone");

        using var locked = await client.SendAsync(stale);

        locked.IsSuccessStatusCode.Should().BeTrue();
        capture.Last!.Headers.Contains(DekHeaderHandler.HeaderName).Should().BeFalse(
            "with the session locked, even a pre-tagged stale header is stripped");

        using var fresh = new HttpRequestMessage(HttpMethod.Get, "/v1/user/sources");
        fresh.Headers.TryAddWithoutValidation(DekHeaderHandler.HeaderName, "stale-key-from-a-retry-clone");
        store.SetDek(Dek);

        using var unlocked = await client.SendAsync(fresh);

        unlocked.IsSuccessStatusCode.Should().BeTrue();
        capture.Last!.Headers.GetValues(DekHeaderHandler.HeaderName)
            .Should().ContainSingle("exactly one value reaches the server").Which.Should().Be(Dek);
    }

    [Fact]
    public void TokenStore_Clear_AlsoClearsDek()
    {
        var store = new TokenStore();
        store.Set(SpaTestContext.FakeJwt(role: "User"));
        store.SetDek(Dek);
        store.DekStore.IsEmpty.Should().BeFalse();

        store.Clear();

        store.AccessToken.Should().BeNull("logout drops the access token (§13.10)");
        store.DekStore.IsEmpty.Should().BeTrue(
            "and the M2 DEK with it — one lifecycle governs both");
        store.DekStore.DekBase64.Should().BeNull();
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static HttpClient ClientFor(TokenStore store, HttpMessageHandler inner)
        => new(new DekHeaderHandler(store)
        {
            InnerHandler = inner,
        })
        {
            BaseAddress = new Uri("http://localhost/"),
        };

    /// <summary>Terminates the chain and keeps the request exactly as it was sent.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            });
        }
    }
}
