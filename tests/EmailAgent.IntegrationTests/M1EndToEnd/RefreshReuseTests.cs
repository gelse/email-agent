using System.Net;
using System.Text.Json;
using EmailAgent.Core.Audit;
using EmailAgent.Infrastructure;
using EmailAgent.IntegrationTests.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmailAgent.IntegrationTests.M1EndToEnd;

// ---------------------------------------------------------------------------
// M1T9 (task step 4): the 4-step refresh-reuse flow over HTTP — login →
// rotate → rotate again → replay the rotated-out cookie. Asserts the 401,
// the `refresh_reuse_detected` audit row, and family-wide revocation (exit
// criterion 3's evidence row).
// ---------------------------------------------------------------------------

/// <summary>
/// Replaying a rotated-out <c>ea_refresh</c> cookie must return RFC 7807 401,
/// write <c>refresh_reuse_detected</c>, and set <c>revoked_at</c> on every row
/// in the affected <c>family_id</c> partition.
/// </summary>
[Trait("Category", "M1EndToEnd")]
[Collection("Postgres")]
public class RefreshReuseTests
{
    private readonly PostgresFixture _postgres;

    public RefreshReuseTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task RotatedToken_Reuse_EmitsAudit_AndRevokesFamily()
    {
        var connectionString = await AccountTestHost.MigrateAsync(_postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        await AdminTestHost.EnsureRolesAsync(factory);
        using var client = AccountTestHost.CreateClient(factory);

        var email = $"m1t9-reuse-{Guid.NewGuid():N}@example.test";
        var userId = await AccountTestHost.SeedUserAsync(
            factory, email, mustChangePassword: false, withDek: true);

        // 1. Login → cookie 1.
        var login = await AccountTestHost.PostLoginAsync(
            client, email, AccountTestHost.TempPassword);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var cookie1 = AccountTestHost.RefreshCookieFrom(login);

        // 2. /v1/account/refresh → cookie 2.
        var rotate1 = await client.SendAsync(AccountTestHost.RefreshRequest(cookie1));
        rotate1.StatusCode.Should().Be(HttpStatusCode.OK);
        var cookie2 = AccountTestHost.RefreshCookieFrom(rotate1);
        cookie2.Should().NotBe(cookie1, "the refresh cookie rotates");
        AccountTestHost.AssertRefreshCookieAttributes(rotate1);

        // 3. /v1/account/refresh with cookie 2 → cookie 3.
        var rotate2 = await client.SendAsync(AccountTestHost.RefreshRequest(cookie2));
        rotate2.StatusCode.Should().Be(HttpStatusCode.OK);
        var cookie3 = AccountTestHost.RefreshCookieFrom(rotate2);
        cookie3.Should().NotBe(cookie2);

        // 4. Replay cookie 1 (rotated-out) → 401 + cookie cleared.
        var reuse = await client.SendAsync(AccountTestHost.RefreshRequest(cookie1));
        var problem = await AccountTestHost.ReadProblemAsync(reuse, HttpStatusCode.Unauthorized);
        problem.GetProperty("type").GetString().Should().Be("/errors/unauthorized");
        AccountTestHost.AssertCookieCleared(reuse);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();

            // The audit row exists and is chained (32-byte hmac). Scoped to
            // THIS test's user: the shared collection database also holds
            // sibling tests' refresh_reuse_detected rows (TargetId = a token
            // id, not a user id — match via the refresh_tokens join below),
            // so an unscoped NotBeEmpty could pass without this flow writing
            // its own evidence row.
            var reuseRows = await db.AuditLog.AsNoTracking()
                .Where(a => a.Action == AuditActions.RefreshReuseDetected)
                .Where(a => db.RefreshTokens.Any(
                    r => r.UserId == userId && a.TargetId == r.Id.ToString()))
                .ToListAsync();
            reuseRows.Should().NotBeEmpty("this flow's reuse evidence row is written");
            reuseRows.Should().OnlyContain(a => a.Hmac != null && a.Hmac.Length == 32);

            // Every token this test issued (one family) is revoked. Scoped by
            // user because the shared collection database also holds sibling
            // tests' families.
            var familyRows = await db.RefreshTokens.AsNoTracking()
                .Where(r => r.UserId == userId)
                .ToListAsync();
            familyRows.Should().NotBeEmpty();
            familyRows.Should().OnlyContain(
                r => r.RevokedAt != null,
                "reuse revokes every token in the family_id partition");
        }

        // The chain stays valid after the reuse write.
        using (var scope = factory.Services.CreateScope())
        {
            var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
            (await audit.VerifyChainAsync(CancellationToken.None)).Should().BeNull();
        }

        // A further refresh with cookie 3 is also refused (family is dead).
        var postReuse = await client.SendAsync(AccountTestHost.RefreshRequest(cookie3));
        await AccountTestHost.ReadProblemAsync(postReuse, HttpStatusCode.Unauthorized);
    }
}
