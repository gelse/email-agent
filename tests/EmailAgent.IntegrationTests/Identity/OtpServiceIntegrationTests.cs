using System.Text;
using EmailAgent.Core.Identity;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using EmailAgent.Infrastructure.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EmailAgent.IntegrationTests.Identity;

/// <summary>
/// M1T5: <see cref="OtpService"/> against real Postgres — issue/verify
/// round-trip, single-use consume, expiry, revocation, admin reissue
/// (Q-A9) and the concurrent single-winner race the task file schedules as
/// <c>Verify_ConcurrentCalls_OnlyOneSucceeds</c>.
///
/// These cases are DB-backed (and listed under the task's unit section) for
/// the same reason M1T4's refresh-store cases moved: the EF in-memory
/// provider cannot host <c>EmailAgentDbContext</c> (User.KdfParams/JsonDocument
/// and the inet IPAddress columns are provider-specific types it refuses to
/// map), and the conditional-consume semantics are only meaningful under
/// real READ COMMITTED row locking. All original assertions are preserved.
/// Verification reads go through fresh scopes so they observe committed
/// state, like a subsequent request would.
/// </summary>
[Collection("Postgres")]
public class OtpServiceIntegrationTests(PostgresFixture postgres)
{
    private static readonly SemaphoreSlim MigrationGate = new(1, 1);
    private static string? _migratedConnectionString;

    [Fact]
    public async Task Issue_Verify_HappyPath_RoundTrip()
    {
        var connectionString = await MigrateAsync();
        await using var factory = ApiTestFactory.Create(connectionString);

        using var scope = factory.Services.CreateScope();
        var otpService = scope.ServiceProvider.GetRequiredService<IOtpService>();
        var userId = await SeedUserAsync(factory, "otp-happy@example.test");

        var issued = await otpService.IssueAsync(userId, Guid.NewGuid(), CancellationToken.None);

        issued.RawOtp.Should().HaveLength(16, "Q-A2 fixes the OTP at 16 base32 chars");
        issued.UserId.Should().Be(userId);
        issued.Id.Should().NotBe(Guid.Empty);
        issued.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow, "the default lifetime is 7 days (Q-A2)");

        (await otpService.VerifyAsync(userId, issued.RawOtp, CancellationToken.None))
            .Should().BeTrue("the issued OTP must verify exactly once");

        // Fresh scope = the committed-state view a later request would get.
        using var verifyScope = factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();

        var row = await db.OneTimePasswords.SingleAsync(o => o.Id == issued.Id);
        row.UsedAt.Should().NotBeNull("a successful verify marks used_at (single-use)");
        row.RevokedAt.Should().BeNull("verification does not revoke");
    }

    [Fact]
    public async Task Verify_SameOTPTwice_SecondReturnsFalse()
    {
        var connectionString = await MigrateAsync();
        await using var factory = ApiTestFactory.Create(connectionString);

        using var scope = factory.Services.CreateScope();
        var otpService = scope.ServiceProvider.GetRequiredService<IOtpService>();
        var userId = await SeedUserAsync(factory, "otp-twice@example.test");

        var issued = await otpService.IssueAsync(userId, Guid.NewGuid(), CancellationToken.None);

        (await otpService.VerifyAsync(userId, issued.RawOtp, CancellationToken.None))
            .Should().BeTrue("the first presentation consumes the OTP");
        (await otpService.VerifyAsync(userId, issued.RawOtp, CancellationToken.None))
            .Should().BeFalse("the second presentation of a used OTP must fail (single-use, Q-A2)");
    }

    [Fact]
    public async Task Verify_WrongOTP_ReturnsFalse()
    {
        var connectionString = await MigrateAsync();
        await using var factory = ApiTestFactory.Create(connectionString);

        using var scope = factory.Services.CreateScope();
        var otpService = scope.ServiceProvider.GetRequiredService<IOtpService>();
        var userId = await SeedUserAsync(factory, "otp-wrong@example.test");

        var issued = await otpService.IssueAsync(userId, Guid.NewGuid(), CancellationToken.None);

        (await otpService.VerifyAsync(userId, "AAAAAAAAAAAAAAAA", CancellationToken.None))
            .Should().BeFalse("a wrong candidate must not verify");

        // Wrong shape (length/charset) is rejected before any PBKDF2 work.
        (await otpService.VerifyAsync(userId, "short", CancellationToken.None))
            .Should().BeFalse("a malformed candidate must not verify");
        (await otpService.VerifyAsync(userId, "aaaaaaaaaaaaaaaa", CancellationToken.None))
            .Should().BeFalse("lowercase is outside the base32 alphabet (Q-A2)");

        // The real OTP is still live — a miss must not consume it.
        (await otpService.VerifyAsync(userId, issued.RawOtp, CancellationToken.None))
            .Should().BeTrue("a failed guess must leave the active OTP usable");
    }

    [Fact]
    public async Task Verify_AfterRevoke_ReturnsFalse()
    {
        var connectionString = await MigrateAsync();
        await using var factory = ApiTestFactory.Create(connectionString);

        using var scope = factory.Services.CreateScope();
        var otpService = scope.ServiceProvider.GetRequiredService<IOtpService>();
        var userId = await SeedUserAsync(factory, "otp-revoke@example.test");

        var issued = await otpService.IssueAsync(userId, Guid.NewGuid(), CancellationToken.None);

        (await otpService.RevokeAllForUserAsync(userId, CancellationToken.None))
            .Should().Be(1, "revocation reports how many live rows it invalidated");

        (await otpService.VerifyAsync(userId, issued.RawOtp, CancellationToken.None))
            .Should().BeFalse("a revoked OTP must never verify (SQ-6 / Q-A9)");

        // Idempotent: nothing live left to revoke.
        (await otpService.RevokeAllForUserAsync(userId, CancellationToken.None))
            .Should().Be(0);
    }

    [Fact]
    public async Task Verify_AfterExpire_ReturnsFalse()
    {
        var connectionString = await MigrateAsync();
        await using var factory = ApiTestFactory.Create(connectionString);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var userId = await SeedUserAsync(factory, "otp-expired@example.test");

        // Negative lifetime: the row is born past expires_at — the server-side
        // lifetime check must refuse it (same technique as M1T4's expired-
        // refresh-token case).
        var service = new OtpService(
            db,
            Options.Create(new OtpOptions { Lifetime = TimeSpan.FromMinutes(-5) }),
            NullLogger<OtpService>.Instance);

        var issued = await service.IssueAsync(userId, Guid.NewGuid(), CancellationToken.None);

        (await service.VerifyAsync(userId, issued.RawOtp, CancellationToken.None))
            .Should().BeFalse("an expired OTP must never verify (Q-A2)");

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var row = await verifyDb.OneTimePasswords.SingleAsync(o => o.Id == issued.Id);
        row.UsedAt.Should().BeNull("expiry must not consume the row");
        row.RevokedAt.Should().BeNull("expiry is not revocation");
    }

    [Fact]
    public async Task Regenerate_RevokesPriorUnused_And_IssuesNew()
    {
        var connectionString = await MigrateAsync();
        await using var factory = ApiTestFactory.Create(connectionString);

        using var scope = factory.Services.CreateScope();
        var otpService = scope.ServiceProvider.GetRequiredService<IOtpService>();
        var userId = await SeedUserAsync(factory, "otp-regen@example.test");
        var adminId = Guid.NewGuid();

        var first = await otpService.IssueAsync(userId, adminId, CancellationToken.None);
        var second = await otpService.RegenerateAsync(userId, adminId, CancellationToken.None);

        second.Id.Should().NotBe(first.Id, "reissue mints a new row");
        second.RawOtp.Should().NotBe(first.RawOtp, "reissue mints a fresh random OTP");
        second.UserId.Should().Be(userId);

        // Task AC: exactly one active OTP after regenerate.
        using var verifyScope = factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();

        var activeCount = await db.OneTimePasswords.CountAsync(o =>
            o.UserId == userId
            && o.UsedAt == null
            && o.RevokedAt == null
            && o.ExpiresAt > DateTimeOffset.UtcNow);
        activeCount.Should().Be(1, "Regenerate leaves a single active OTP (Q-A9)");

        var oldRow = await db.OneTimePasswords.SingleAsync(o => o.Id == first.Id);
        oldRow.RevokedAt.Should().NotBeNull("the prior unused OTP is revoked, not deleted");
        oldRow.UsedAt.Should().BeNull("revocation is not consumption");

        var newRow = await db.OneTimePasswords.SingleAsync(o => o.Id == second.Id);
        newRow.CreatedBy.Should().Be(adminId, "the issuing admin is recorded");
        newRow.RevokedAt.Should().BeNull();
        newRow.UsedAt.Should().BeNull();

        // The old OTP is dead; the new one verifies.
        var verifyOldScope = factory.Services.CreateScope();
        var verifyOldService = verifyOldScope.ServiceProvider.GetRequiredService<IOtpService>();
        (await verifyOldService.VerifyAsync(userId, first.RawOtp, CancellationToken.None))
            .Should().BeFalse("the revoked OTP must not verify");
        (await verifyOldService.VerifyAsync(userId, second.RawOtp, CancellationToken.None))
            .Should().BeTrue("the replacement OTP must verify");
    }

    [Fact]
    public async Task Regenerate_Concurrent_DoubleClick_LeavesExactlyOneActive()
    {
        var connectionString = await MigrateAsync();
        await using var factory = ApiTestFactory.Create(connectionString);

        using var scopeA = factory.Services.CreateScope();
        using var scopeB = factory.Services.CreateScope();
        var storeA = scopeA.ServiceProvider.GetRequiredService<IOtpService>();
        var storeB = scopeB.ServiceProvider.GetRequiredService<IOtpService>();

        var userId = await SeedUserAsync(factory, "otp-regen-race@example.test");
        var adminId = Guid.NewGuid();

        // Admin double-click on the M1T7 reissue endpoint: even with zero
        // live rows to revoke (nothing to lock via the revoke UPDATE), the
        // per-user FOR UPDATE lock must serialize the two revoke+insert
        // pairs so the single-active invariant survives.
        var results = await Task.WhenAll(
            storeA.RegenerateAsync(userId, adminId, CancellationToken.None),
            storeB.RegenerateAsync(userId, adminId, CancellationToken.None));

        results.Should().HaveCount(2, "both regenerates complete (serialized, not failed)");
        results[0].Id.Should().NotBe(results[1].Id, "each call mints its own row");

        using var verifyScope = factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();

        var activeCount = await db.OneTimePasswords.CountAsync(o =>
            o.UserId == userId
            && o.UsedAt == null
            && o.RevokedAt == null
            && o.ExpiresAt > DateTimeOffset.UtcNow);
        activeCount.Should().Be(1, "concurrent reissues must never leave two active OTPs");
    }

    [Fact]
    public async Task Verify_ConcurrentCalls_OnlyOneSucceeds()
    {
        var connectionString = await MigrateAsync();
        await using var factory = ApiTestFactory.Create(connectionString);

        using var scopeA = factory.Services.CreateScope();
        using var scopeB = factory.Services.CreateScope();
        var storeA = scopeA.ServiceProvider.GetRequiredService<IOtpService>();
        var storeB = scopeB.ServiceProvider.GetRequiredService<IOtpService>();

        var userId = await SeedUserAsync(factory, "otp-race@example.test");
        var issued = await storeA.IssueAsync(userId, Guid.NewGuid(), CancellationToken.None);

        // Two independent scopes race the same OTP. The conditional consume
        // must elect exactly one winner; the loser gets false.
        var results = await Task.WhenAll(
            storeA.VerifyAsync(userId, issued.RawOtp, CancellationToken.None),
            storeB.VerifyAsync(userId, issued.RawOtp, CancellationToken.None));

        results.Count(r => r)
            .Should().Be(1, "concurrent verifications of one OTP return true at most once");

        using var verifyScope = factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var row = await db.OneTimePasswords.SingleAsync(o => o.Id == issued.Id);
        row.UsedAt.Should().NotBeNull("the winning consume committed used_at");
    }

    [Fact]
    public async Task IssueAsync_RawOtp_Is_Returned_Only_In_IssuedOtp_And_NotPersisted()
    {
        var connectionString = await MigrateAsync();
        await using var factory = ApiTestFactory.Create(connectionString);

        using var scope = factory.Services.CreateScope();
        var otpService = scope.ServiceProvider.GetRequiredService<IOtpService>();
        var userId = await SeedUserAsync(factory, "otp-once@example.test");

        var issued = await otpService.IssueAsync(userId, Guid.NewGuid(), CancellationToken.None);

        issued.RawOtp.Should().MatchRegex("^[A-Z2-7]{16}$");

        // Load the committed row: only the 48-byte salt||hash may exist —
        // the raw OTP string must not appear anywhere in the stored bytes.
        using var verifyScope = factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();

        var row = await db.OneTimePasswords.SingleAsync(o => o.Id == issued.Id);
        row.OtpHash.Should().HaveCount(48, "otp_hash is salt (16) || hash (32) (SQ-5 / Q-A8)");
        ContainsSequence(row.OtpHash, Encoding.ASCII.GetBytes(issued.RawOtp))
            .Should().BeFalse("the raw OTP must never be persisted (Q-A2 view-once)");

        // Sanity: the hash verifies the raw value, proving the row belongs
        // to this issuance even though it cannot yield the OTP back.
        OtpService.VerifyHash(issued.RawOtp, row.OtpHash)
            .Should().BeTrue("the stored salt||hash is the verifier for the returned OTP");
    }

    /// <summary>Contiguous subsequence search — used to prove non-persistence.</summary>
    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length)
        {
            return false;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Seeds a users row — one_time_passwords.user_id is a real FK.</summary>
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
