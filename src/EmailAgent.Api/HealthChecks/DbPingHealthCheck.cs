using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace EmailAgent.Api.HealthChecks;

public sealed class DbPingHealthCheck : IHealthCheck
{
    private readonly IConfiguration _configuration;

    public DbPingHealthCheck(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _configuration.GetConnectionString("Postgres")
            ?? _configuration["ConnectionStrings__Postgres"];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return HealthCheckResult.Unhealthy("Connection string 'Postgres' is not configured.");
        }

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            // Actually round-trip to the server so a stopped/unreachable Postgres
            // is detected even when Npgsql connection pooling hands back a
            // stale pooled connection.
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Postgres is not reachable.", ex);
        }
    }
}
