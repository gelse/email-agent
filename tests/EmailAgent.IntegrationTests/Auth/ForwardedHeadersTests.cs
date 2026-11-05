using System.Net;
using FluentAssertions;
using Xunit;

namespace EmailAgent.IntegrationTests.Auth;

/// <summary>
/// Q-A13: the API speaks plain HTTP internally and honours the edge proxy's
/// <c>X-Forwarded-Proto</c> (<c>UseForwardedHeaders</c> in Program.cs), so
/// scheme-sensitive logic — here the M1T4 Origin gate on
/// <c>/v1/account/refresh</c>, which compares <c>{Scheme}://{Host}</c> against
/// the browser's <c>Origin</c> — sees <c>https</c> exactly as it will behind
/// nginx (dev) / traefik (testing). No Postgres needed: the gate runs before
/// any endpoint and these requests never carry a cookie into the store.
/// </summary>
public class ForwardedHeadersTests
{
    private const string DevEdgeOrigin = "https://localhost:8443";

    [Fact]
    public async Task Refresh_Path_Treats_XForwardedProto_Https_As_Https_SameOrigin()
    {
        await using var factory = ApiTestFactory.Create();
        using var client = factory.CreateClient();

        // The dev browser's view: HTTPS at the nginx edge (Host forwarded by
        // nginx as $http_host), plain HTTP from nginx to api — the scheme
        // only arrives via X-Forwarded-Proto.
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/account/refresh");
        request.Headers.Host = "localhost:8443";
        request.Headers.Add("Origin", DevEdgeOrigin);
        request.Headers.Add("X-Forwarded-Proto", "https");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().NotBe(
            HttpStatusCode.Forbidden,
            "with X-Forwarded-Proto: https honoured, {Scheme}://{Host} is "
                + $"{DevEdgeOrigin} and the same-origin Origin clears the gate");
    }

    [Fact]
    public async Task Refresh_Path_Rejects_Https_Origin_When_No_Forwarded_Proto()
    {
        await using var factory = ApiTestFactory.Create();
        using var client = factory.CreateClient();

        // Control: without the forwarded header the request really is plain
        // HTTP internally, so the https Origin must NOT match (this is the
        // exact mismatch the dev stack had before Q-A13 was resolved).
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/account/refresh");
        request.Headers.Host = "localhost:8443";
        request.Headers.Add("Origin", DevEdgeOrigin);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "http://localhost:8443 vs Origin " + DevEdgeOrigin + " is cross-scheme");
    }

    [Fact]
    public async Task Refresh_Path_Still_Rejects_Foreign_Origin_When_Forwarded_Proto_Is_Https()
    {
        await using var factory = ApiTestFactory.Create();
        using var client = factory.CreateClient();

        // Forwarded scheme must widen nothing: a foreign origin stays 403.
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/account/refresh");
        request.Headers.Host = "localhost:8443";
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Add("X-Forwarded-Proto", "https");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync())
            .Should().Contain("/errors/forbidden");
    }
}
