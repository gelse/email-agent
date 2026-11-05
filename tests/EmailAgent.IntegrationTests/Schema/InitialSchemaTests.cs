using EmailAgent.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmailAgent.IntegrationTests.Schema;

/// <summary>
/// Shares one Testcontainers Postgres across the schema tests and keeps them
/// out of the parallel phase (M1T2 task step 13).
/// </summary>
[CollectionDefinition("Postgres", DisableParallelization = true)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
}

/// <summary>
/// M1T2 acceptance: applies the InitialSchema migration to a Testcontainers
/// Postgres and asserts the resulting schema against §6 (tables, extensions,
/// column types, nullability, unique/check constraints, partial indexes).
/// </summary>
[Collection("Postgres")]
public class InitialSchemaTests
{
    private static readonly SemaphoreSlim MigrationGate = new(1, 1);
    private static string? _migratedConnectionString;

    private readonly PostgresFixture _postgres;

    public InitialSchemaTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task Extensions_Citext_And_Pgcrypto_AreInstalled()
    {
        var connectionString = await MigrateAsync();
        await SchemaAssertions.AssertExtensionsInstalled(connectionString);
    }

    [Theory]
    [InlineData("users")]
    [InlineData("one_time_passwords")]
    [InlineData("model_catalog")]
    [InlineData("message_sources")]
    [InlineData("agent_configs")]
    [InlineData("action_configs")]
    [InlineData("scheduled_jobs")]
    [InlineData("processed_messages")]
    [InlineData("pending_actions")]
    [InlineData("user_notifications")]
    [InlineData("audit_log")]
    [InlineData("encrypted_blobs")]
    [InlineData("refresh_tokens")]
    public async Task Table_Exists_For_Every_ArchitectureTable(string table)
    {
        var connectionString = await MigrateAsync();
        SchemaAssertions.RequiredTables.Should().Contain(table);
        await SchemaAssertions.AssertTableExists(connectionString, table);
    }

    [Theory]
    [InlineData("users")]
    [InlineData("one_time_passwords")]
    [InlineData("model_catalog")]
    [InlineData("message_sources")]
    [InlineData("agent_configs")]
    [InlineData("action_configs")]
    [InlineData("scheduled_jobs")]
    [InlineData("processed_messages")]
    [InlineData("pending_actions")]
    [InlineData("user_notifications")]
    [InlineData("audit_log")]
    [InlineData("encrypted_blobs")]
    [InlineData("refresh_tokens")]
    public async Task Column_Types_Match_SchemaSpec(string table)
    {
        var connectionString = await MigrateAsync();
        await SchemaAssertions.AssertColumnsMatchSpec(connectionString, table);
    }

    [Fact]
    public async Task Users_KeyMaterialColumns_AreNullable()
    {
        // SQ-11 / Q-E8: kdf_salt and kdf_params are NULL until set-passphrase,
        // matching dek_wrapped / dek_nonce (Q-E6).
        var connectionString = await MigrateAsync();
        foreach (var column in new[] { "kdf_salt", "kdf_params", "dek_wrapped", "dek_nonce" })
        {
            await SchemaAssertions.AssertColumnIsNullable(connectionString, "users", column, expectedNullable: true);
        }
    }

    [Fact]
    public async Task Users_RoleStatus_HaveCheckConstraints()
    {
        var connectionString = await MigrateAsync();
        await SchemaAssertions.AssertCheckConstraintValues(
            connectionString, "users", "Admin", "User");
        await SchemaAssertions.AssertCheckConstraintValues(
            connectionString, "users", "Pending", "Active", "Disabled");
    }

    [Fact]
    public async Task ProcessedMessages_UniqueConstraint_On_SourceIdRemoteUid()
    {
        var connectionString = await MigrateAsync();
        await SchemaAssertions.AssertUniqueConstraint(
            connectionString, "processed_messages", "source_id", "remote_uid");
    }

    [Fact]
    public async Task Users_Email_Is_Citext_Unique()
    {
        var connectionString = await MigrateAsync();
        await SchemaAssertions.AssertColumnIsNullable(connectionString, "users", "email", expectedNullable: false);

        var emailType = await SchemaAssertions.GetColumnTypeAsync(connectionString, "users", "email");
        emailType.Should().Be("citext");

        await SchemaAssertions.AssertUniqueConstraint(connectionString, "users", "email");
    }

    [Fact]
    public async Task RefreshTokens_TokenHash_Is_Unique()
    {
        var connectionString = await MigrateAsync();
        await SchemaAssertions.AssertUniqueConstraint(connectionString, "refresh_tokens", "token_hash");

        // SQ-1: NOT NULL on the family/issuance columns too (task step 12).
        foreach (var column in new[] { "token_hash", "user_id", "family_id", "expires_at" })
        {
            await SchemaAssertions.AssertColumnIsNullable(connectionString, "refresh_tokens", column, expectedNullable: false);
        }
    }

    [Fact]
    public async Task OneTimePasswords_RevokedAt_Column_Exists()
    {
        // SQ-6 / Q-A9.
        var connectionString = await MigrateAsync();
        var type = await SchemaAssertions.GetColumnTypeAsync(connectionString, "one_time_passwords", "revoked_at");
        type.Should().Be("timestamp with time zone");
        await SchemaAssertions.AssertColumnIsNullable(connectionString, "one_time_passwords", "revoked_at", expectedNullable: true);
    }

    [Fact]
    public async Task AuditLog_Hmac_IsByteaNotNull()
    {
        var connectionString = await MigrateAsync();
        var type = await SchemaAssertions.GetColumnTypeAsync(connectionString, "audit_log", "hmac");
        type.Should().Be("bytea");
        await SchemaAssertions.AssertColumnIsNullable(connectionString, "audit_log", "hmac", expectedNullable: false);
    }

    [Fact]
    public async Task PartialIndexes_Exist_ForQueueAndUnreadQueries()
    {
        // Hand-emitted raw SQL in the migration (M1T2 task step 11) — guard it.
        var connectionString = await MigrateAsync();
        await SchemaAssertions.AssertIndexExists(
            connectionString, "pending_actions", "ix_pending_actions_status_requested", requireFilter: true);
        await SchemaAssertions.AssertIndexExists(
            connectionString, "scheduled_jobs", "ix_scheduled_jobs_status_scheduled_for", requireFilter: true);
        await SchemaAssertions.AssertIndexExists(
            connectionString, "user_notifications", "ix_user_notifications_unread", requireFilter: true);
    }

    [Fact]
    public async Task ProcessedMessages_Stage_HasDefaultFetched()
    {
        // §6: `stage` text NOT NULL DEFAULT 'fetched' (Q-O9).
        var connectionString = await MigrateAsync();
        await SchemaAssertions.AssertColumnDefaultContains(
            connectionString, "processed_messages", "stage", "fetched");
    }

    [Fact]
    public async Task ForeignKey_DeleteBehaviors_MatchDesign()
    {
        // Pins the M1T2 FK matrix: Cascade for user-owned children (SQ-1 for
        // refresh_tokens), Restrict for actor/blob references (Q-LP2 for
        // audit_log; §6/Q-O15 retention for processed_messages.source_id),
        // and NO FK for the two soft references (§6 labels them without FK;
        // Q-API47 orphan-blob posture).
        var connectionString = await MigrateAsync();

        await SchemaAssertions.AssertForeignKeyAction(
            connectionString, "refresh_tokens", "user_id", "ON DELETE CASCADE");
        await SchemaAssertions.AssertForeignKeyAction(
            connectionString, "refresh_tokens", "replaced_by_id", "ON DELETE RESTRICT");
        await SchemaAssertions.AssertForeignKeyAction(
            connectionString, "audit_log", "actor_user_id", "ON DELETE RESTRICT");
        await SchemaAssertions.AssertForeignKeyAction(
            connectionString, "processed_messages", "source_id", "ON DELETE RESTRICT");

        await SchemaAssertions.AssertNoForeignKeyOnColumn(
            connectionString, "encrypted_blobs", "owner_user_id");
        await SchemaAssertions.AssertNoForeignKeyOnColumn(
            connectionString, "one_time_passwords", "created_by");
    }

    [Fact]
    public async Task AuditLog_CreatedAtIndex_SortsDescending()
    {
        // §6 recency pattern: audit_log(created_at DESC). EF models the
        // index as descending; pin the physical DDL so an EF/Npgsql
        // convention change cannot silently flip the sort order.
        var connectionString = await MigrateAsync();
        await SchemaAssertions.AssertIndexDefinitionContains(
            connectionString, "audit_log", "ix_audit_log_created_at", "created_at DESC");
    }

    /// <summary>
    /// Applies the InitialSchema migration once per container; subsequent
    /// tests re-use the migrated database (EF marks it in
    /// <c>__EFMigrationsHistory</c>, so a repeat call would be a no-op anyway).
    /// </summary>
    private async Task<string> MigrateAsync()
    {
        var connectionString = _postgres.ConnectionString;

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