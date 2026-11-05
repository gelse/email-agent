using Testcontainers.PostgreSql;
using Xunit;

namespace EmailAgent.IntegrationTests;

/// <summary>
/// A Postgres 16 container (same image as <c>docker/docker-compose.dev.yml</c>)
/// started before and torn down after the test class that receives it.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
