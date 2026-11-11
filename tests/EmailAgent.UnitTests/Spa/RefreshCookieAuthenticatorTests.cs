using System.Net;
using System.Text;
using EmailAgent.Spa.Auth;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Spa;

/// <summary>
/// M2T6 review fix (round 2, WARNING), migrated by M5T1: <c>ea_refresh</c> is
/// one browser-wide cookie, so a refresh issued by this tab can return the
/// token of an account signed in from another tab. The authenticator must
/// compare subjects before storing: a different principal goes through
/// <see cref="TokenStore.SignIn"/>, which drops the crypto-session token this
/// session was holding, while the same principal keeps its unlock (no
/// pointless passphrase re-prompt every 15 minutes).
/// </summary>
public class RefreshCookieAuthenticatorTests
{
    private const string SessionToken = "m5t1-session-token";

    private const string SameSubject =
        "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task Refresh_SameSubject_KeepsCryptoSession()
    {
        var store = new TokenStore();
        store.Set(JwtWithSubject(SameSubject));
        store.CryptoSession.SetToken(SessionToken);
        using var refresh = RefreshRespondingWith(JwtWithSubject(SameSubject));
        var authenticator = Authenticator(store, refresh);

        (await authenticator.AttemptSilentRefreshAsync()).Should().BeTrue();

        store.CryptoSession.IsEmpty.Should().BeFalse(
            "the same signed-in user keeps the unlock they already hold");
        store.CryptoSession.Token.Should().Be(SessionToken);
    }

    [Fact]
    public async Task Refresh_DifferentSubject_DropsCryptoSession()
    {
        var store = new TokenStore();
        store.Set(JwtWithSubject(SameSubject));
        store.CryptoSession.SetToken(SessionToken);
        var otherUserToken = JwtWithSubject("99999999-9999-9999-9999-999999999999");
        using var refresh = RefreshRespondingWith(otherUserToken);
        var authenticator = Authenticator(store, refresh);

        (await authenticator.AttemptSilentRefreshAsync()).Should().BeTrue(
            "the refresh itself succeeded — a new token was stored");

        store.AccessToken.Should().Be(otherUserToken);
        store.CryptoSession.IsEmpty.Should().BeTrue(
            "another account's unlock must never survive into this account's session");
        store.CryptoSession.Token.Should().BeNull();
    }

    [Fact]
    public async Task Refresh_NoStoredToken_StoresAndLeavesStoreEmpty()
    {
        // Cold start: there is no session to compare against, so the token is
        // stored plainly — and no unlock can be stale because none is held.
        var store = new TokenStore();
        using var refresh = RefreshRespondingWith(JwtWithSubject(SameSubject));
        var authenticator = Authenticator(store, refresh);

        (await authenticator.AttemptSilentRefreshAsync()).Should().BeTrue();

        store.AccessToken.Should().Be(JwtWithSubject(SameSubject));
        store.CryptoSession.IsEmpty.Should().BeTrue();
    }

    /// <summary>
    /// M2T6 review fix (round 3, NIT): the comparison fails CLOSED. A token
    /// whose subject cannot be read is not provably the same principal, so
    /// the unlock is dropped — a false drop costs one re-prompt, a false keep
    /// would pair this session's requests with another account's session.
    /// </summary>
    [Fact]
    public async Task Refresh_UnparseableSubject_DropsCryptoSession()
    {
        var store = new TokenStore();
        store.Set(JwtWithSubject(SameSubject));
        store.CryptoSession.SetToken(SessionToken);
        var subjectless = JwtWithoutSubject();
        using var refresh = RefreshRespondingWith(subjectless);
        var authenticator = Authenticator(store, refresh);

        (await authenticator.AttemptSilentRefreshAsync()).Should().BeTrue(
            "the refresh itself succeeded — a new token was stored");

        store.AccessToken.Should().Be(subjectless);
        store.CryptoSession.IsEmpty.Should().BeTrue(
            "an unparseable sub is treated as 'not the same principal' (fail closed)");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static RefreshCookieAuthenticator Authenticator(
        TokenStore store, HttpMessageHandler refresh)
        => new(new FixedClientFactory(refresh), store);

    private static HttpMessageHandler RefreshRespondingWith(string accessToken)
        => new ResponderHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"access_token\":\"{accessToken}\",\"expires_in\":900}}",
                Encoding.UTF8,
                "application/json"),
        });

    /// <summary>An unsigned compact JWT carrying <paramref name="subject"/> — the SPA parses, never verifies (M1T8).</summary>
    private static string JwtWithSubject(string subject)
    {
        static string B64Url(string value)
            => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

        return $"{B64Url("{\"alg\":\"HS256\",\"typ\":\"JWT\"}")}"
            + $".{B64Url($"{{\"sub\":\"{subject}\",\"role\":\"User\"}}")}.c2ln";
    }

    /// <summary>An otherwise well-formed compact JWT with no <c>sub</c> claim (contract-drift shape).</summary>
    private static string JwtWithoutSubject()
    {
        static string B64Url(string value)
            => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

        return $"{B64Url("{\"alg\":\"HS256\",\"typ\":\"JWT\"}")}"
            + $".{B64Url("{\"role\":\"User\"}")}.c2ln";
    }

    private sealed class ResponderHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response());
    }

    private sealed class FixedClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost/"),
            };
    }
}
