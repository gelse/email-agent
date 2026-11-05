using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EmailAgent.Api.Endpoints;
using EmailAgent.Core.Audit;
using EmailAgent.Core.Security;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using EmailAgent.IntegrationTests.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmailAgent.IntegrationTests.M1EndToEnd;

// ---------------------------------------------------------------------------
// M1T9 (task steps 3 + 6): the xUnit mirror of scripts/smoke-m1.sh — the full
// first-login flow against Testcontainers Postgres + the real host. One [Fact]
// per smoke step, with the milestone Verification-strategy assertions. The
// suite shares the "Postgres" collection (sequential) so it owns one container
// and never races sibling suites on the migration gate.
// ---------------------------------------------------------------------------

/// <summary>
/// xUnit version of the M1T9 bash smoke: admin login → create Alice (OTP
/// once) → admin logout → Alice OTP-login → change-password → set-passphrase →
/// DEK re-derivation → logout → re-login. Covers milestone exit criteria 1
/// and 2.
/// </summary>
[Trait("Category", "M1EndToEnd")]
[Collection("Postgres")]
public class M1EndToEndTests
{
    private readonly PostgresFixture _postgres;

    public M1EndToEndTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Admin_CreatesUser_AliceLogsInAndCompletesFirstLogin_Success()
    {
        var (factory, client) = await Harness.CreateAsync(_postgres);
        await using var disposableFactory = factory;

        var adminEmail = Unique("admin");
        var aliceEmail = Unique("alice");
        await Harness.SeedAdminAsync(factory, adminEmail);

        // Step 3: admin login (form) → 200 + bearer + first-login flag off.
        var adminLogin = await AccountTestHost.PostLoginAsync(
            client, adminEmail, AccountTestHost.TempPassword);
        adminLogin.StatusCode.Should().Be(HttpStatusCode.OK);
        var adminBody = await AccountTestHost.ReadJsonAsync(adminLogin);
        var adminBearer = adminBody.GetProperty("access_token").GetString()!;
        adminBody.GetProperty("must_complete_first_login").GetBoolean().Should().BeFalse();

        // Step 4: create Alice → 201 with exactly one OTP (Q-A3 view-once).
        var create = await client.SendAsync(
            AdminTestHost.Request(
                HttpMethod.Post, "/v1/admin/users", adminBearer,
                new { email = aliceEmail, role = "User" }));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var createBody = await AccountTestHost.ReadJsonAsync(create);
        var aliceId = Guid.Parse(createBody.GetProperty("id").GetString()!);
        var otp = createBody.GetProperty("otp").GetString()!;
        otp.Should().MatchRegex("^[A-Z2-7]{16}$");
        var rawCreate = await create.Content.ReadAsStringAsync();
        ((rawCreate.Length - rawCreate.Replace(otp, string.Empty).Length) / otp.Length)
            .Should().Be(1, "the OTP appears exactly once in the response body");

        var alice = await AccountTestHost.LoadUserAsync(factory, aliceId);
        alice.Status.Should().Be(UserStatus.Pending);
        alice.MustChangePassword.Should().BeTrue();
        alice.DekWrapped.Should().BeNull("admin-created accounts carry no key material (Q-E6)");

        // Step 5: admin logout.
        var adminCookie = AccountTestHost.RefreshCookieFrom(adminLogin);
        var adminLogout = await client.SendAsync(AccountTestHost.LogoutRequest(adminCookie));
        adminLogout.StatusCode.Should().Be(HttpStatusCode.NoContent);
        AccountTestHost.AssertCookieCleared(adminLogout);

        // Step 6: Alice OTP-login → first-login flag + X-Login-State.
        var otpLogin = await AccountTestHost.PostOtpLoginAsync(client, aliceEmail, otp);
        otpLogin.StatusCode.Should().Be(HttpStatusCode.OK);
        var otpBody = await AccountTestHost.ReadJsonAsync(otpLogin);
        otpBody.GetProperty("must_complete_first_login").GetBoolean().Should().BeTrue(
            "dek_wrapped IS NULL until set-passphrase");
        otpLogin.Headers.GetValues(AccountEndpoints.LoginStateHeader).Single()
            .Should().Be(AccountEndpoints.MustCompleteFirstLoginState);
        AccountTestHost.AssertRefreshCookieAttributes(otpLogin);
        var aliceBearer = otpBody.GetProperty("access_token").GetString()!;
        var aliceCookie = AccountTestHost.RefreshCookieFrom(otpLogin);

        // A reused OTP is refused (single-use, Q-A2).
        var reused = await AccountTestHost.PostOtpLoginAsync(client, aliceEmail, otp);
        await AccountTestHost.ReadProblemAsync(reused, HttpStatusCode.Unauthorized);

        // Step 7: first-login change-password (no old_password).
        var change = await AccountTestHost.PostJsonAsync(
            client,
            "/v1/account/change-password",
            new
            {
                new_password = AccountTestHost.NewPassword,
                new_password_confirm = AccountTestHost.NewPassword,
            },
            aliceBearer);
        change.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var afterChange = await AccountTestHost.LoadUserAsync(factory, aliceId);
        afterChange.MustChangePassword.Should().BeFalse();
        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            (await userManager.CheckPasswordAsync(afterChange, AccountTestHost.NewPassword))
                .Should().BeTrue("the new password verifies through Identity");
        }

        // Step 8: set-passphrase (two inputs) → key material persisted.
        var setPassphrase = await AccountTestHost.PostJsonAsync(
            client,
            "/v1/account/set-passphrase",
            new
            {
                passphrase = AccountTestHost.Passphrase,
                passphrase_confirm = AccountTestHost.Passphrase,
            },
            aliceBearer);
        setPassphrase.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var aliceAfter = await AccountTestHost.LoadUserAsync(factory, aliceId);
        aliceAfter.KdfSalt.Should().NotBeNull().And.HaveCount(16);
        aliceAfter.DekWrapped.Should().NotBeNull().And.HaveCount(48);
        aliceAfter.DekNonce.Should().NotBeNull().And.HaveCount(12);
        aliceAfter.KdfParams.Should().NotBeNull();

        // Step 9 + 10: logout, then password re-login (proves the change).
        var logout = await client.SendAsync(AccountTestHost.LogoutRequest(aliceCookie));
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var relogin = await AccountTestHost.PostLoginAsync(
            client, aliceEmail, AccountTestHost.NewPassword);
        relogin.StatusCode.Should().Be(HttpStatusCode.OK);
        var reloginBody = await AccountTestHost.ReadJsonAsync(relogin);
        reloginBody.GetProperty("must_complete_first_login").GetBoolean().Should().BeFalse(
            "must_change_password is false and dek_wrapped is set");

        // Step 11: audit rows for every event in the flow.
        var actions = (await AccountTestHost.AuditRowsAsync(factory, aliceId))
            .Select(row => row.Action)
            .ToList();
        actions.Should().Contain(new[]
        {
            AuditActions.AdminCreateUser,
            AuditActions.AdminOtpIssued,
            AuditActions.OtpLoginSuccess,
            AuditActions.ChangePassword,
            AuditActions.SetPassphrase,
            AuditActions.Logout,
            AuditActions.LoginSuccess,
        });
        (await AccountTestHost.AuditRowsAsync(factory, aliceId))
            .Should().OnlyContain(row => row.Hmac != null && row.Hmac.Length == 32);
    }

    [Fact]
    public async Task SetPassphrase_PersistsDekWrapped_AndUnwrapsWithSamePassphrase()
    {
        var (factory, client) = await Harness.CreateAsync(_postgres);
        await using var disposableFactory = factory;

        // Clean set → re-derive → unwrap round-trip. (The wrong-passphrase
        // rejection lives on the unwrap path — asserted below; set-passphrase
        // itself is first-time only and never checks an existing wrap.)
        var email = Unique("dek-roundtrip");
        var userId = await AccountTestHost.SeedUserAsync(factory, email);
        var bearer = await AccountTestHost.LoginAsync(client, email);

        var set = await AccountTestHost.PostJsonAsync(
            client,
            "/v1/account/set-passphrase",
            new
            {
                passphrase = AccountTestHost.Passphrase,
                passphrase_confirm = AccountTestHost.Passphrase,
            },
            bearer);
        set.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var user = await AccountTestHost.LoadUserAsync(factory, userId);
        user.DekWrapped.Should().NotBeNull("dek_wrapped is set after set-passphrase (EC2)");
        using var kdfDoc = user.KdfParams;
        var kdf = kdfDoc!.RootElement.Deserialize<KdfParams>()
            ?? throw new InvalidOperationException("stored kdf_params must parse");

        using var scope = factory.Services.CreateScope();
        var encryption = scope.ServiceProvider.GetRequiredService<IEncryptionService>();

        // Re-derive the KEK from the SAME passphrase + the row's salt/params
        // and unwrap — the DEK must come back byte-for-byte.
        var dek = encryption.UnwrapDek(
            userId, AccountTestHost.Passphrase, user.KdfSalt!, kdf,
            user.DekWrapped!, user.DekNonce!);
        dek.Should().HaveCount(32, "the DEK is 256 bits");

        // A second unwrap with the same inputs is deterministic (same KEK,
        // same ciphertext) — this is the "re-logging in derives KEK and
        // unwraps DEK successfully" criterion made explicit.
        var dekAgain = encryption.UnwrapDek(
            userId, AccountTestHost.Passphrase, user.KdfSalt!, kdf,
            user.DekWrapped!, user.DekNonce!);
        dekAgain.Should().Equal(dek);

        // Wrong passphrase must fail typed (not a silent bad DEK).
        var wrong = () => encryption.UnwrapDek(
            userId, "definitely-not-the-passphrase", user.KdfSalt!, kdf,
            user.DekWrapped!, user.DekNonce!);
        wrong.Should().Throw<InvalidPassphraseException>(
            "a GCM tag mismatch under a wrong KEK is a wrong passphrase");

        // Repeat set-passphrase → 409 /errors/passphrase-already-set (EC2).
        var repeat = await AccountTestHost.PostJsonAsync(
            client,
            "/v1/account/set-passphrase",
            new
            {
                passphrase = AccountTestHost.Passphrase,
                passphrase_confirm = AccountTestHost.Passphrase,
            },
            bearer);
        var problem = await AccountTestHost.ReadProblemAsync(repeat, HttpStatusCode.Conflict);
        problem.GetProperty("type").GetString().Should().Be("/errors/passphrase-already-set");
    }

    [Fact]
    public async Task AuditLog_ContainsAllAuthEvents_WithValidHmacChain()
    {
        var (factory, client) = await Harness.CreateAsync(_postgres);
        await using var disposableFactory = factory;

        var adminEmail = Unique("audit-admin");
        var aliceEmail = Unique("audit-alice");
        await Harness.SeedAdminAsync(factory, adminEmail);

        var adminLogin = await AccountTestHost.PostLoginAsync(
            client, adminEmail, AccountTestHost.TempPassword);
        adminLogin.StatusCode.Should().Be(HttpStatusCode.OK);
        var adminBearer = (await AccountTestHost.ReadJsonAsync(adminLogin))
            .GetProperty("access_token").GetString()!;

        var create = await client.SendAsync(
            AdminTestHost.Request(
                HttpMethod.Post, "/v1/admin/users", adminBearer,
                new { email = aliceEmail }));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var createBody = await AccountTestHost.ReadJsonAsync(create);
        var aliceId = Guid.Parse(createBody.GetProperty("id").GetString()!);
        var otp = createBody.GetProperty("otp").GetString()!;

        var adminCookie = AccountTestHost.RefreshCookieFrom(adminLogin);
        (await client.SendAsync(AccountTestHost.LogoutRequest(adminCookie)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var otpLogin = await AccountTestHost.PostOtpLoginAsync(client, aliceEmail, otp);
        otpLogin.StatusCode.Should().Be(HttpStatusCode.OK);
        var aliceBearer = (await AccountTestHost.ReadJsonAsync(otpLogin))
            .GetProperty("access_token").GetString()!;
        var aliceCookie = AccountTestHost.RefreshCookieFrom(otpLogin);

        (await AccountTestHost.PostJsonAsync(
            client,
            "/v1/account/change-password",
            new
            {
                new_password = AccountTestHost.NewPassword,
                new_password_confirm = AccountTestHost.NewPassword,
            },
            aliceBearer)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await AccountTestHost.PostJsonAsync(
            client,
            "/v1/account/set-passphrase",
            new
            {
                passphrase = AccountTestHost.Passphrase,
                passphrase_confirm = AccountTestHost.Passphrase,
            },
            aliceBearer)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await client.SendAsync(AccountTestHost.LogoutRequest(aliceCookie)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Step 11: rows for each event, non-null 32-byte hmac + actor + ip.
        var aliceRows = await AccountTestHost.AuditRowsAsync(factory, aliceId);
        aliceRows.Select(r => r.Action).Should().Contain(new[]
        {
            AuditActions.AdminCreateUser,
            AuditActions.AdminOtpIssued,
            AuditActions.OtpLoginSuccess,
            AuditActions.ChangePassword,
            AuditActions.SetPassphrase,
            AuditActions.Logout,
        });
        aliceRows.Should().OnlyContain(r =>
            r.ActorUserId != null
            && r.Ip != null
            && r.Hmac != null
            && r.Hmac.Length == 32,
            "every auth-event row carries actor_user_id, ip and a full HMAC");

        // Step 12: the canonical chain walk (the same helper tools/AuditVerify
        // uses against the live DB) returns null = chain intact.
        using var scope = factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
        (await audit.VerifyChainAsync(CancellationToken.None)).Should().BeNull(
            "the HMAC chain stays intact after the full flow");
    }

    private static string Unique(string prefix)
        => $"m1t9-{prefix}-{Guid.NewGuid():N}@example.test";

    /// <summary>Migrated DB + factory + cookie-less HttpClient (one per test).</summary>
    private static class Harness
    {
        public static async Task<(WebApplicationFactory<Program> Factory, HttpClient Client)>
            CreateAsync(PostgresFixture postgres)
        {
            var connectionString = await AccountTestHost.MigrateAsync(postgres);
            var factory = AccountTestHost.CreateFactory(connectionString);
            await AdminTestHost.EnsureRolesAsync(factory);
            var client = AccountTestHost.CreateClient(factory);
            return (factory, client);
        }

        public static async Task SeedAdminAsync(WebApplicationFactory<Program> factory, string email)
        {
            using var scope = factory.Services.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = new User
            {
                Id = Guid.NewGuid(),
                Email = email,
                UserName = email,
                Role = UserRole.Admin,
                Status = UserStatus.Active,
                MustChangePassword = false,
                CreatedAt = DateTimeOffset.UtcNow,
                // Dummy key material so the admin's first login is not a
                // forced first-login flow (parity with the SPA suite).
                KdfSalt = new byte[16],
                KdfParams = JsonSerializer.SerializeToDocument(KdfParams.Default()),
                DekWrapped = new byte[48],
                DekNonce = new byte[12],
            };

            var created = await userManager.CreateAsync(user, AccountTestHost.TempPassword);
            created.Succeeded.Should().BeTrue(
                "seeding must succeed: "
                + string.Join(", ", created.Errors.Select(e => e.Description)));
            (await userManager.AddToRoleAsync(user, "Admin")).Succeeded.Should().BeTrue(
                "the Admin role row must exist (AdminTestHost.EnsureRolesAsync)");
        }
    }
}