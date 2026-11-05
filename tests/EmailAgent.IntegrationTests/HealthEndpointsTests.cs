using System.Net;
using FluentAssertions;
using Xunit;

namespace EmailAgent.IntegrationTests;

/// <summary>
/// M1T1 health-endpoint tests that need no database.
/// </summary>
public class HealthEndpointsTests
{
    [Fact]
    public async Task Health_Live_Returns200()
    {
        await using var factory = ApiTestFactory.Create();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Health_Ready_BeforePostgresReady_Returns503()
    {
        // Port 1 on loopback refuses immediately, so the db readiness check
        // observes an unreachable Postgres without contacting any external
        // service (AGENTS.md hard constraint on tests).
        const string unreachablePostgres =
            "Host=127.0.0.1;Port=1;Database=emailagent;Username=emailagent;Password=unreachable";

        await using var factory = ApiTestFactory.Create(unreachablePostgres);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"db\":\"Unhealthy\"");
        body.Should().Contain("\"blob-volume\":\"Healthy\"");
    }
}
