using System.Net;
using EmailAgent.Core.Audit;
using EmailAgent.Core.Errors;
using EmailAgent.Core.Identity;
using EmailAgent.Core.Options;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Audit;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using EmailAgent.Infrastructure.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace EmailAgent.IntegrationTests.Identity;

/// <summary>
/// M1T4 task step 10: <see cref="RefreshTokenStore"/> issuance/lookup
/// round-trip, rotation semantics and reuse detection.
///
/// The task file scheduled these as in-memory unit tests; the EF in-memory
/// provider cannot host this context (User.KdfParams/JsonDocument and the
/// inet IPAddress columns are provider-specific types it refuses to map, it
/// has no transactions, and the reuse path's audit write needs the
/// pg_advisory_xact_lock raw SQL), and the reuse test asserts a persisted,
/// HMAC-chained audit row — a real database is the only honest host. All
/// assertions are the task's originals; verification reads go through fresh
/// scopes so they observe committed state, like a subsequent request would.
/// </summary>
[Collection("Postgres")]
public class RefreshTokenStoreTests(PostgresFixture postgres)
{
    private static readonly SemaphoreSlim MigrationGate = new(1, 1);
    private static string? _migratedConnectionString;

    [Fact]
    public async Task Issue_Then_FindByHash_Returns_Same_Record()
    {
        var connectionString = await MigrateAsync();
        await using var factory = ApiTestFactory.Create(connectionString);

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRefreshTokenStore>();
        var userId = await SeedUserAsync(factory, "rt-issue@example.test");

        var issued = await store.IssueAsync(userId, "test-agent", "203.0.113.9", CancellationToken.None);

        issued.RawToken.Should().NotBeNullOrWhiteSpace("the raw token is returned to the API layer");
        issued.Record.UserId.Should().Be(userId);
        issued.Record.UsedAt.Should().BeNull();
        issued.Record.RevokedAt.Should().BeNull();
        issued.Record.FamilyId.Should().NotBe(Guid.Empty);

        // Fresh scope = the committed-state view a later request would get.
        using var verifyScope = factory.Services.CreateScope();
        var verifyStore = verifyScope.ServiceProvider.GetRequiredService<IRefreshTokenStore>();

        var found = await verifyStore.FindByHashAsync(issued.Record.TokenHash, CancellationToken.None);
        found.Should().NotBeNull("the SHA-256 of the raw token is the lookup key");
        found!.Id.Should().Be(issued.Record.Id);
        found.FamilyId.Should().Be(issued.Record.FamilyId);
        found.Ip.Should().Be(IPAddress.Parse("203.0.113.9"));

        var miss = await verifyStore.FindByHashAsync(new byte[32], CancellationToken.None);
        miss.Should().BeNull("an unknown hash is a miss");
    }

    [Fact]
    public async Task Rotate_Marks_Old_UsedAt_And_Issues_New_In_Same_Family()
    {
        var connectionString = await MigrateAsync();
        await using var factory = ApiTestFactory.Create(connectionString);

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRefreshTokenStore>();

        var first = await store.IssueAsync(
            await SeedUserAsync(factory, "rt-rotate@example.test"),
            "agent", "198.51.100.4", CancellationToken.None);
        var second = await store.RotateAsync(first.Record, CancellationToken.None);

        second.Record.FamilyId.Should().Be(first.Record.FamilyId, "rotation stays in the same family");
        second.Record.Id.Should().NotBe(first.Record.Id);
        second.RawToken.Should().NotBe(first.RawToken, "every rotation mints a fresh 256-bit token");
        second.Record.UsedAt.Should().BeNull();

        using var verifyScope = factory.Services.CreateScope();
        var verifyStore = verifyScope.ServiceProvider.GetRequiredService<IRefreshTokenStore>();

        var reloaded = await verifyStore.FindByHashAsync(first.Record.TokenHash, CancellationToken.None);
        reloaded!.UsedAt.Should().NotBeNull("the consumed row is marked used");
        reloaded.ReplacedById.Should().Be(second.Record.Id, "the chain links old → new");

        // Second rotation continues the chain.
        var third = await store.RotateAsync(second.Record, CancellationToken.None);
        third.Record.FamilyId.Should().Be(first.Record.FamilyId);
        (await verifyStore.FindByHashAsync(second.Record.TokenHash, CancellationToken.None))!
            .UsedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Rotate_Twice_Second_Call_Throws_Reuse_And_Revokes_Family()
    {
        var connectionString = await MigrateAsync();
        await using var factory = ApiTestFactory.Create(connectionString);

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRefreshTokenStore>();

        var first = await store.IssueAsync(
            await SeedUserAsync(factory, "rt-reuse@example.test"),
            "agent", "198.51.100.5", CancellationToken.None);
        var second = await store.RotateAsync(first.Record, CancellationToken.None);

        // Re-presenting the already-rotated token is reuse (§13.11 / Q-API5).
        var rotateAgain = () => store.RotateAsync(first.Record, CancellationToken.None);

        await rotateAgain.Should().ThrowAsync<RefreshTokenReuseException>();

        // Fresh scope: committed family state as a later request sees it.
        using var verifyScope = factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();

        var familyRows = await db.RefreshTokens
            .Where(r => r.FamilyId == first.Record.FamilyId)
            .ToListAsync();
        familyRows.Should().NotBeEmpty();
        familyRows.Should().OnlyContain(
            r => r.RevokedAt != null,
            "reuse revokes every token in the family_id partition (exit criterion 3)");

        // The successor is dead too — a live session cannot outlive reuse.
        var verifyStore = verifyScope.ServiceProvider.GetRequiredService<IRefreshTokenStore>();
        var successor = await verifyStore.FindByHashAsync(second.Record.TokenHash, CancellationToken.None);
        successor!.RevokedAt.Should().NotBeNull();

        // The reuse evidence was written (scoped to this test's token — the
        // shared collection database also holds sibling tests' reuse rows).
        var auditRow = await db.AuditLog.SingleAsync(a =>
            a.Action == "refresh_reuse_detected"
            && a.TargetId == first.Record.Id.ToString());
        auditRow.Hmac.Should().NotBeNull("the audit row is HMAC-chained");
        auditRow.TargetId.Should().Be(first.Record.Id.ToString());
    }

    [Fact]
    public async Task Revoke_Marks_Token_RevokedAt()
    {
        var connectionString = await MigrateAsync();
        await using var factory = ApiTestFactory.Create(connectionString);

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRefreshTokenStore>();

        var issued = await store.IssueAsync(
            await SeedUserAsync(factory, "rt-revoke@example.test"),
            "agent", "198.51.100.6", CancellationToken.None);
        await store.RevokeAsync(issued.Record.TokenHash, CancellationToken.None);

        using var verifyScope = factory.Services.CreateScope();
        var verifyStore = verifyScope.ServiceProvider.GetRequiredService<IRefreshTokenStore>();

        var reloaded = await verifyStore.FindByHashAsync(issued.Record.TokenHash, CancellationToken.None);
        reloaded!.RevokedAt.Should().NotBeNull("logout marks revoked_at, it does not delete the row");

        // Revoking twice is idempotent.
        await verifyStore.RevokeAsync(issued.Record.TokenHash, CancellationToken.None);
        (await verifyStore.FindByHashAsync(issued.Record.TokenHash, CancellationToken.None))!
            .RevokedAt.Should().Be(reloaded.RevokedAt);
    }

    [Fact]
    public async Task Rotate_ExpiredToken_Refused_Without_Consume_Or_Revocation()
    {
        var connectionString = await MigrateAsync();
        await using var factory = ApiTestFactory.Create(connectionString);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
        var auditOptions = scope.ServiceProvider.GetRequiredService<IOptions<AuditOptions>>();
        var userId = await SeedUserAsync(factory, "rt-expired@example.test");

        // Negative TTL: the row is born past expires_at — the server-side
        // AUTH__RefreshTokenTtl check must refuse it (review WARNING: TTL
        // enforced nowhere).
        var store = new RefreshTokenStore(
            db,
            audit,
            auditOptions,
            Options.Create(new AuthOptions { RefreshTokenTtl = TimeSpan.FromMinutes(-5) }));

        var issued = await store.IssueAsync(userId, "agent", "198.51.100.7", CancellationToken.None);

        var rotate = () => store.RotateAsync(issued.Record, CancellationToken.None);
        await rotate.Should().ThrowAsync<RefreshTokenExpiredException>();

        // No consume, no revocation, no successor, no reuse-evidence row:
        // expiry is normal end-of-life, not theft.
        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();

        var familyRows = await verifyDb.RefreshTokens
            .Where(r => r.FamilyId == issued.Record.FamilyId)
            .ToListAsync();
        familyRows.Should().ContainSingle("no successor may be minted from an expired token");
        familyRows[0].UsedAt.Should().BeNull("expiry must not consume the row");
        familyRows[0].RevokedAt.Should().BeNull("expiry is not reuse — no family revocation");

        (await verifyDb.AuditLog.AnyAsync(a =>
                a.Action == AuditActions.RefreshReuseDetected
                && a.TargetId == issued.Record.Id.ToString()))
            .Should().BeFalse("expiry is not reuse — no refresh_reuse_detected row");
    }

    [Fact]
    public async Task Rotate_Concurrently_ExactlyOne_Winner_And_Loser_Revokes_Family()
    {
        var connectionString = await MigrateAsync();
        await using var factory = ApiTestFactory.Create(connectionString);

        using var scopeA = factory.Services.CreateScope();
        using var scopeB = factory.Services.CreateScope();
        var storeA = scopeA.ServiceProvider.GetRequiredService<IRefreshTokenStore>();
        var storeB = scopeB.ServiceProvider.GetRequiredService<IRefreshTokenStore>();

        var userId = await SeedUserAsync(factory, "rt-race@example.test");
        var first = await storeA.IssueAsync(userId, "agent", "198.51.100.8", CancellationToken.None);

        // Two independent scopes race the same token (stolen-token replay vs
        // legitimate rotation). The conditional consume must elect one winner;
        // the loser takes the reuse path.
        static async Task<int> Attempt(IRefreshTokenStore store, RefreshTokenRecord record)
        {
            try
            {
                await store.RotateAsync(record, CancellationToken.None);
                return 1;
            }
            catch (RefreshTokenReuseException)
            {
                return 0;
            }
        }

        var attempts = new[]
        {
            Attempt(storeA, first.Record),
            Attempt(storeB, first.Record),
        };
        var wins = (await attempts[0]) + (await attempts[1]);

        wins.Should().Be(1, "exactly one rotation may win; the other presenter must be treated as reuse");

        using var verifyScope = factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var familyRows = await db.RefreshTokens
            .Where(r => r.FamilyId == first.Record.FamilyId)
            .ToListAsync();
        familyRows.Should().NotBeEmpty();
        familyRows.Should().OnlyContain(
            r => r.RevokedAt != null,
            "two presenters of one token = reuse: the loser revokes the whole family");
    }

    /// <summary>Seeds a users row — refresh_tokens.user_id is a real FK.</summary>
    private static async Task<Guid> SeedUserAsync(
        WebApplicationFactory<Program> factory,
        string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserName = email,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(),
            Role = UserRole.User,
            Status = UserStatus.Active,
            PasswordHash = "not-a-real-hash-integration-fixture",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>Applies every migration once per shared Postgres container.</summary>
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
