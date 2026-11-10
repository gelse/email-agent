using EmailAgent.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmailAgent.IntegrationTests.Schema;

/// <summary>
/// M1T4 / SQ-10 acceptance: the IdentitySchema migration adds the six
/// Identity support tables under their default <c>AspNet*</c> names alongside
/// the §6 tables, and the <c>users</c> table gains Identity's housekeeping
/// columns (asserted via <see cref="SchemaAssertions.ColumnSpec"/> in
/// <see cref="InitialSchemaTests"/>).
/// </summary>
[Collection("Postgres")]
public class IdentitySchemaTests(PostgresFixture postgres)
{
    private static readonly SemaphoreSlim MigrationGate = new(1, 1);
    private static string? _migratedConnectionString;

    [Theory]
    [InlineData("AspNetRoles")]
    [InlineData("AspNetRoleClaims")]
    [InlineData("AspNetUserRoles")]
    [InlineData("AspNetUserClaims")]
    [InlineData("AspNetUserLogins")]
    [InlineData("AspNetUserTokens")]
    public async Task IdentityTable_Exists(string table)
    {
        var connectionString = await MigrateAsync();
        await SchemaAssertions.AssertTableExists(connectionString, table);
    }

    [Fact]
    public async Task Roles_NormalizedName_Is_Unique()
    {
        var connectionString = await MigrateAsync();
        await SchemaAssertions.AssertIndexExists(
            connectionString, "AspNetRoles", "ux_aspnet_roles_normalized_name", requireFilter: false);
        await SchemaAssertions.AssertColumnIsNullable(
            connectionString, "AspNetRoles", "normalized_name", expectedNullable: false);
    }

    [Fact]
    public async Task UserRoles_Cascade_To_Users_And_Roles()
    {
        var connectionString = await MigrateAsync();
        await SchemaAssertions.AssertForeignKeyAction(
            connectionString, "AspNetUserRoles", "user_id", "ON DELETE CASCADE");
        await SchemaAssertions.AssertForeignKeyAction(
            connectionString, "AspNetUserRoles", "role_id", "ON DELETE CASCADE");
    }

    /// <summary>Same one-time migration gate as the other schema test classes.</summary>
    private async Task<string> MigrateAsync()
    {
        var connectionString = postgres.ConnectionString;

        await MigrationGate.WaitAsync();
        try
        {
            if (_migratedConnectionString != connectionString)
            {
                var options = new DbContextOptionsBuilder<EmailAgentDbContext>()
                    .UseNpgsql(connectionString)
                    .Options;

                await using var database = new EmailAgentDbContext(options);
                await database.Database.MigrateAsync();
                _migratedConnectionString = connectionString;
            }

            return connectionString;
        }
        finally
        {
            MigrationGate.Release();
        }
    }
}
