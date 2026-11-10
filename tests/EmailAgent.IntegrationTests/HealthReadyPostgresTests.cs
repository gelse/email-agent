using System.Net;
using FluentAssertions;
using Xunit;

namespace EmailAgent.IntegrationTests;

/// <summary>
/// M1T1 readiness test against a healthy Postgres (Testcontainers).
/// </summary>
public class HealthReadyPostgresTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Health_Ready_AfterPostgresHealthy_Returns200()
    {
        await using var factory = ApiTestFactory.Create(postgres.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"db\":\"Healthy\"");
        body.Should().Contain("\"blob-volume\":\"Healthy\"");
    }
}
