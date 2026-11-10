using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EmailAgent.Api.Auth;
using EmailAgent.Api.Endpoints;
using EmailAgent.Core.Audit;
using EmailAgent.Core.Identity;
using EmailAgent.Core.Security;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmailAgent.IntegrationTests.Endpoints;

// ---------------------------------------------------------------------------
// Shared harness for the M1T6 account-endpoint suite (one class per endpoint,
// per the task file). Tests run against Testcontainers Postgres + the real
// host; users are seeded through UserManager because M1T7's admin API does
// not exist yet (task test-setup note). Cookies are handled manually: the
// ea_refresh cookie is Secure + Path=/v1/account/refresh, which the default
// cookie container would drop over the factory's http:// base address.
// ---------------------------------------------------------------------------

internal static class AccountTestHost
{
    public const string TempPassword = "TempPassw0rd!xyz";
    public const string NewPassword = "NewPassw0rd!abc";
    public const string WrongPassword = "WrongPassw0rd!1";
    public const string Passphrase = "correct horse battery staple";

    /// <summary>appsettings.json AUTH__AccessTokenTtl = 00:15:00.</summary>
    public const int AccessTokenTtlSeconds = 900;

    private static readonly SemaphoreSlim MigrationGate = new(1, 1);
    private static string? _migratedConnectionString;

    /// <summary>Applies every migration once per shared Postgres container.</summary>
    public static async Task<string> MigrateAsync(PostgresFixture postgres)
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

    public static WebApplicationFactory<Program> CreateFactory(string connectionString)
        => ApiTestFactory.Create(connectionString).WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddTransient<IStartupFilter, AccountTestStartupFilter>()));

    public static HttpClient CreateClient(WebApplicationFactory<Program> factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    /// <summary>
    /// Creates a user directly through UserManager (Identity hash +
    /// normalization included). <paramref name="withDek"/> plants dummy key
    /// material so <c>must_complete_first_login</c> evaluates to false without
    /// running the passphrase flow (login never touches crypto).
    /// </summary>
    public static async Task<Guid> SeedUserAsync(
        WebApplicationFactory<Program> factory,
        string email,
        string? password = TempPassword,
        UserStatus status = UserStatus.Active,
        bool mustChangePassword = false,
        bool withDek = false)
    {
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserName = email,
            Role = UserRole.User,
            Status = status,
            MustChangePassword = mustChangePassword,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        if (withDek)
        {
            user.KdfSalt = new byte[16];
            user.KdfParams = JsonSerializer.SerializeToDocument(KdfParams.Default());
            user.DekWrapped = new byte[48];
            user.DekNonce = new byte[12];
        }

        var result = password is null
            ? await userManager.CreateAsync(user)
            : await userManager.CreateAsync(user, password);
        result.Succeeded.Should().BeTrue(
            "seeding must succeed: "
                + string.Join(", ", result.Errors.Select(e => e.Description)));
        return user.Id;
    }

    public static Task<HttpResponseMessage> PostLoginAsync(
        HttpClient client,
        string email,
        string password,
        string? testIp = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/account/login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["email"] = email,
                ["password"] = password,
            }),
        };
        if (testIp is not null)
        {
            request.Headers.Add("X-Test-IP", testIp);
        }

        return client.SendAsync(request);
    }

    /// <summary>Logs in and returns the access token (asserts 200).</summary>
    public static async Task<string> LoginAsync(
        HttpClient client,
        string email,
        string password = TempPassword)
    {
        var response = await PostLoginAsync(client, email, password);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "login succeeds for a seeded user");
        var body = await ReadJsonAsync(response);
        var token = body.GetProperty("access_token").GetString();
        token.Should().NotBeNullOrWhiteSpace();
        return token!;
    }

    public static Task<HttpResponseMessage> PostOtpLoginAsync(
        HttpClient client,
        string email,
        string otp)
        => client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/v1/account/otp-login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["email"] = email,
                ["otp"] = otp,
            }),
        });

    public static Task<HttpResponseMessage> PostJsonAsync(
        HttpClient client,
        string path,
        object payload,
        string? bearer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return client.SendAsync(request);
    }

    public static HttpRequestMessage RefreshRequest(string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/account/refresh");
        request.Headers.Add("Cookie", $"ea_refresh={cookie}");
        return request;
    }

    public static HttpRequestMessage LogoutRequest(string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/account/logout");
        request.Headers.Add("Cookie", $"ea_refresh={cookie}");
        return request;
    }

    /// <summary>Extracts the raw ea_refresh value from a Set-Cookie header.</summary>
    public static string RefreshCookieFrom(HttpResponseMessage response)
    {
        response.Headers.TryGetValues("Set-Cookie", out var values)
            .Should().BeTrue("login/refresh must set the ea_refresh cookie");

        foreach (var value in values!)
        {
            const string prefix = "ea_refresh=";
            var index = value.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var raw = value[(index + prefix.Length)..];
            var end = raw.IndexOf(';');
            var cookie = end < 0 ? raw : raw[..end];
            cookie.Should().NotBeNullOrWhiteSpace("the cookie carries the raw refresh token");
            return cookie;
        }

        false.Should().BeTrue("an ea_refresh cookie was present in Set-Cookie");
        return null!; // unreachable — assertion above throws
    }

    /// <summary>SQ-4 / Q-A7 cookie attributes on a written refresh cookie.</summary>
    public static void AssertRefreshCookieAttributes(HttpResponseMessage response)
    {
        response.Headers.TryGetValues("Set-Cookie", out var values).Should().BeTrue();
        var setCookie = string.Join("\n", values!);
        setCookie.Should().MatchRegex("(?i)ea_refresh=");
        setCookie.Should().MatchRegex("(?i)path=/v1/account/refresh");
        setCookie.Should().MatchRegex("(?i)httponly");
        setCookie.Should().MatchRegex("(?i)secure");
        setCookie.Should().MatchRegex("(?i)samesite=lax");
    }

    /// <summary>Logout/reuse must expire the cookie (Max-Age=0, task step 9).</summary>
    public static void AssertCookieCleared(HttpResponseMessage response)
    {
        response.Headers.TryGetValues("Set-Cookie", out var values).Should().BeTrue();
        var setCookie = string.Join("\n", values!);
        setCookie.Should().MatchRegex("(?i)ea_refresh=");
        var expired = setCookie.Contains("max-age=0", StringComparison.OrdinalIgnoreCase)
            || setCookie.Contains("1970", StringComparison.OrdinalIgnoreCase);
        expired.Should().BeTrue(
            $"the ea_refresh cookie must be expired immediately, Set-Cookie was: {setCookie}");
    }

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>Asserts the RFC 7807 envelope and returns the parsed document.</summary>
    public static async Task<JsonElement> ReadProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus)
    {
        response.StatusCode.Should().Be(expectedStatus);
        response.Content.Headers.ContentType?.ToString().Should()
            .Contain("application/problem+json", "every 4xx/5xx is RFC 7807 (§13.12)");

        var problem = await ReadJsonAsync(response);
        problem.GetProperty("type").GetString().Should().StartWith("/errors/");
        problem.GetProperty("title").GetString().Should().NotBeNullOrWhiteSpace();
        problem.GetProperty("status").GetInt32().Should().Be((int)expectedStatus);
        problem.GetProperty("instance").GetString().Should().NotBeNullOrWhiteSpace();
        return problem;
    }

    public static async Task<User> LoadUserAsync(WebApplicationFactory<Program> factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId);
    }

    /// <summary>All audit rows this test's user authored (target or actor).</summary>
    public static async Task<List<AuditLog>> AuditRowsAsync(
        WebApplicationFactory<Program> factory,
        Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return await db.AuditLog.AsNoTracking()
            .Where(a => a.TargetId == userId.ToString() || a.ActorUserId == userId)
            .OrderBy(a => a.Id)
            .ToListAsync();
    }

    public static async Task<string> IssueOtpAsync(
        WebApplicationFactory<Program> factory,
        Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var otpService = scope.ServiceProvider.GetRequiredService<IOtpService>();
        var issued = await otpService.IssueAsync(userId, Guid.NewGuid(), CancellationToken.None);
        return issued.RawOtp;
    }

    /// <summary>
    /// The Q-O7 fixed windows are epoch-aligned (window ticks =
    /// floor(now/60s)); starting a 6-attempt burst in the first ~55 s of a
    /// window keeps the whole sequence inside one window so a wall-clock
    /// boundary can never reset the budget mid-test (review finding 3a).
    /// </summary>
    public static async Task WaitForFreshRateLimitWindowAsync()
    {
        var secondsIntoWindow = DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 60;
        if (secondsIntoWindow > 55)
        {
            await Task.Delay(TimeSpan.FromSeconds(60 - secondsIntoWindow + 1));
        }
    }

    public static async Task ExpireOtpAsync(WebApplicationFactory<Program> factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        await db.OneTimePasswords
            .Where(o => o.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(
                o => o.ExpiresAt, DateTimeOffset.UtcNow.AddDays(-1)));
    }
}

// ---------------------------------------------------------------------------
// POST /v1/account/login
// ---------------------------------------------------------------------------

[Collection("Postgres")]
public class LoginEndpointTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Login_HappyPath_ReturnsTokenAndSetsCookie_WithMustCompleteFirstLoginFalse()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        const string email = "login-happy@example.test";
        var userId = await AccountTestHost.SeedUserAsync(
            factory, email, mustChangePassword: false, withDek: true);

        var response = await AccountTestHost.PostLoginAsync(
            client, email, AccountTestHost.TempPassword);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await AccountTestHost.ReadJsonAsync(response);
        body.GetProperty("access_token").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("expires_in").GetInt32().Should().Be(AccountTestHost.AccessTokenTtlSeconds);
        body.GetProperty("must_complete_first_login").GetBoolean().Should().BeFalse();
        response.Headers.Contains(AccountEndpoints.LoginStateHeader).Should().BeFalse(
            "X-Login-State appears only when the first-login flow must run");
        AccountTestHost.AssertRefreshCookieAttributes(response);
        response.Headers.Contains("X-RateLimit-Limit").Should().BeFalse(
            "§13.14: unauthenticated login responses carry no X-RateLimit-*");

        var rows = await AccountTestHost.AuditRowsAsync(factory, userId);
        rows.Should().ContainSingle(a => a.Action == AuditActions.LoginSuccess)
            .Which.ActorUserId.Should().Be(userId);
    }

    [Fact]
    public async Task Login_FirstLogin_ReturnsMustCompleteFirstLoginTrue_AndXLoginStateHeader()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        const string email = "login-first-login@example.test";
        var userId = await AccountTestHost.SeedUserAsync(
            factory, email, mustChangePassword: true, withDek: false);

        var response = await AccountTestHost.PostLoginAsync(
            client, email, AccountTestHost.TempPassword);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the status is always 200 — the SPA routes on body/header");
        var body = await AccountTestHost.ReadJsonAsync(response);
        body.GetProperty("must_complete_first_login").GetBoolean().Should().BeTrue(
            "must_change_password is true and dek_wrapped IS NULL");
        response.Headers.GetValues(AccountEndpoints.LoginStateHeader).Single()
            .Should().Be(AccountEndpoints.MustCompleteFirstLoginState);
        AccountTestHost.AssertRefreshCookieAttributes(response);

        var rows = await AccountTestHost.AuditRowsAsync(factory, userId);
        rows.Should().ContainSingle(a => a.Action == AuditActions.LoginSuccess);
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401_AndWritesAudit()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        const string email = "login-wrong-password@example.test";
        var userId = await AccountTestHost.SeedUserAsync(factory, email);

        var response = await AccountTestHost.PostLoginAsync(
            client, email, AccountTestHost.WrongPassword);

        var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.Unauthorized);
        problem.GetProperty("type").GetString().Should().Be("/errors/unauthorized");

        var rows = await AccountTestHost.AuditRowsAsync(factory, userId);
        rows.Should().ContainSingle(a => a.Action == AuditActions.LoginFailure
                && a.TargetId == userId.ToString())
            .Which.MetadataJson.Should().Contain(email,
                "login_failure carries the email in metadata_json");

        // AC: for an account that does not exist the actor stays null (the
        // email never lands in actor_user_id).
        const string unknownEmail = "login-unknown-account@example.test";
        var unknown = await AccountTestHost.PostLoginAsync(
            client, unknownEmail, AccountTestHost.WrongPassword);
        await AccountTestHost.ReadProblemAsync(unknown, HttpStatusCode.Unauthorized);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var anonymousFailures = await db.AuditLog.AsNoTracking()
            .Where(a => a.Action == AuditActions.LoginFailure && a.TargetId == null)
            .OrderBy(a => a.Id)
            .ToListAsync();
        anonymousFailures.Should().Contain(
            a => (a.MetadataJson ?? string.Empty).Contains(unknownEmail, StringComparison.Ordinal),
            "the unknown email is recorded as metadata");
        anonymousFailures.Should().OnlyContain(a => a.ActorUserId == null,
            "an unknown account has no actor_user_id");
    }

    [Fact]
    public async Task Login_FivePerMinute_PerIP_Sixth_Returns429_WithRetryAfter_ButNoXRateLimitHeaders()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        // One IP, six different accounts: only the per-IP budget can trip.
        await AccountTestHost.WaitForFreshRateLimitWindowAsync();
        const string ip = "198.51.100.10";
        for (var i = 0; i < RateLimitPolicies.LoginPerMinute; i++)
        {
            var attempt = await AccountTestHost.PostLoginAsync(
                client, $"login-per-ip-{i}@example.test", AccountTestHost.WrongPassword, ip);
            attempt.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                $"attempt {i + 1} is within the 5/min per-IP budget");
        }

        var rejected = await AccountTestHost.PostLoginAsync(
            client, "login-per-ip-5@example.test", AccountTestHost.WrongPassword, ip);

        var problem = await AccountTestHost.ReadProblemAsync(
            rejected, HttpStatusCode.TooManyRequests);
        problem.GetProperty("type").GetString().Should().Be("/errors/rate-limited");
        rejected.Headers.RetryAfter.Should().NotBeNull("429s carry Retry-After (§13.14)");
        rejected.Headers.Contains("X-RateLimit-Limit").Should().BeFalse(
            "§13.14: unauthenticated responses carry no X-RateLimit-*");
    }

    [Fact]
    public async Task Login_FivePerMinute_PerAccount_Sixth_Returns429_WithRetryAfter()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        // One account, six different addresses: only the per-account budget
        // can trip (Q-O7 — account-stuffing protection).
        await AccountTestHost.WaitForFreshRateLimitWindowAsync();
        const string email = "login-per-account@example.test";
        for (var i = 0; i < RateLimitPolicies.LoginPerMinute; i++)
        {
            var attempt = await AccountTestHost.PostLoginAsync(
                client, email, AccountTestHost.WrongPassword, $"198.51.100.{i + 1}");
            attempt.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                $"attempt {i + 1} is within the 5/min per-account budget");
        }

        var rejected = await AccountTestHost.PostLoginAsync(
            client, email, AccountTestHost.WrongPassword, "198.51.100.6");

        var problem = await AccountTestHost.ReadProblemAsync(
            rejected, HttpStatusCode.TooManyRequests);
        problem.GetProperty("type").GetString().Should().Be("/errors/rate-limited");
        rejected.Headers.RetryAfter.Should().NotBeNull();
    }
}

// ---------------------------------------------------------------------------
// POST /v1/account/otp-login
// ---------------------------------------------------------------------------

[Collection("Postgres")]
public class OtpLoginEndpointTests(PostgresFixture postgres)
{
    [Fact]
    public async Task OtpLogin_HappyPath_ReturnsToken_AndMarksUserActive_AndForcesPasswordChange()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        const string email = "otp-login-happy@example.test";
        var userId = await AccountTestHost.SeedUserAsync(
            factory, email, status: UserStatus.Pending, mustChangePassword: false);
        var otp = await AccountTestHost.IssueOtpAsync(factory, userId);

        var response = await AccountTestHost.PostOtpLoginAsync(client, email, otp);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await AccountTestHost.ReadJsonAsync(response);
        body.GetProperty("access_token").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("must_complete_first_login").GetBoolean().Should().BeTrue(
            "the formula always evaluates true after OTP login (flag forced)");
        response.Headers.GetValues(AccountEndpoints.LoginStateHeader).Single()
            .Should().Be(AccountEndpoints.MustCompleteFirstLoginState);
        AccountTestHost.AssertRefreshCookieAttributes(response);

        var user = await AccountTestHost.LoadUserAsync(factory, userId);
        user.Status.Should().Be(UserStatus.Active, "Pending → Active on first OTP login");
        user.MustChangePassword.Should().BeTrue("OTP login forces the password change");

        var rows = await AccountTestHost.AuditRowsAsync(factory, userId);
        rows.Should().ContainSingle(a => a.Action == AuditActions.OtpLoginSuccess)
            .Which.ActorUserId.Should().Be(userId);
    }

    [Fact]
    public async Task OtpLogin_ExpiredOtp_Returns401()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        const string email = "otp-login-expired@example.test";
        var userId = await AccountTestHost.SeedUserAsync(factory, email);
        var otp = await AccountTestHost.IssueOtpAsync(factory, userId);
        await AccountTestHost.ExpireOtpAsync(factory, userId);

        var response = await AccountTestHost.PostOtpLoginAsync(client, email, otp);

        var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.Unauthorized);
        problem.GetProperty("type").GetString().Should().Be("/errors/unauthorized");

        var rows = await AccountTestHost.AuditRowsAsync(factory, userId);
        rows.Should().ContainSingle(a => a.Action == AuditActions.OtpLoginFailure);
    }

    [Fact]
    public async Task OtpLogin_ReusedOtp_Returns401_OnSecondUse()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        const string email = "otp-reused@example.test";
        var userId = await AccountTestHost.SeedUserAsync(factory, email);
        var otp = await AccountTestHost.IssueOtpAsync(factory, userId);

        var first = await AccountTestHost.PostOtpLoginAsync(client, email, otp);
        first.StatusCode.Should().Be(HttpStatusCode.OK, "the first use consumes the OTP");

        var second = await AccountTestHost.PostOtpLoginAsync(client, email, otp);
        await AccountTestHost.ReadProblemAsync(second, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Account_OtpLogin_FirstLogin_FlowsToChangePassword()
    {
        // Milestone exit-criteria test (adapted to the task file's flow):
        // OTP login → first-login change-password → password_hash updated and
        // must_change_password flips to false, JWT + refresh cookie in hand.
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        const string email = "otp-first-login-flow@example.test";
        var userId = await AccountTestHost.SeedUserAsync(
            factory, email, status: UserStatus.Pending, mustChangePassword: true);
        var tempHash = (await AccountTestHost.LoadUserAsync(factory, userId)).PasswordHash;
        var otp = await AccountTestHost.IssueOtpAsync(factory, userId);

        var otpResponse = await AccountTestHost.PostOtpLoginAsync(client, email, otp);
        otpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        AccountTestHost.AssertRefreshCookieAttributes(otpResponse);
        var body = await AccountTestHost.ReadJsonAsync(otpResponse);
        var bearer = body.GetProperty("access_token").GetString()!;

        // First-login shape: new_password + new_password_confirm only (no
        // old_password — the OTP session is the credential).
        var change = await AccountTestHost.PostJsonAsync(
            client,
            "/v1/account/change-password",
            new { new_password = AccountTestHost.NewPassword, new_password_confirm = AccountTestHost.NewPassword },
            bearer);
        change.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = await AccountTestHost.LoadUserAsync(factory, userId);
        after.PasswordHash.Should().NotBe(tempHash, "the random temp password is replaced");
        after.MustChangePassword.Should().BeFalse("the forced flag flips on success");

        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        (await userManager.CheckPasswordAsync(after, AccountTestHost.NewPassword)).Should().BeTrue(
            "the new password verifies through Identity");

        var rows = await AccountTestHost.AuditRowsAsync(factory, userId);
        rows.Should().Contain(a => a.Action == AuditActions.OtpLoginSuccess);
        rows.Should().ContainSingle(a => a.Action == AuditActions.ChangePassword);
    }
}

// ---------------------------------------------------------------------------
// POST /v1/account/set-passphrase
// ---------------------------------------------------------------------------

[Collection("Postgres")]
public class SetPassphraseEndpointTests(PostgresFixture postgres)
{
    private async Task<(Guid UserId, string Bearer)> SignInAsync(
        WebApplicationFactory<Program> factory,
        HttpClient client,
        string email)
    {
        var userId = await AccountTestHost.SeedUserAsync(factory, email);
        var bearer = await AccountTestHost.LoginAsync(client, email);
        return (userId, bearer);
    }

    [Fact]
    public async Task SetPassphrase_HappyPath_PersistsDekWrapped_AndNonce_AndSalt()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        const string email = "set-passphrase-happy@example.test";
        var (userId, bearer) = await SignInAsync(factory, client, email);

        var response = await AccountTestHost.PostJsonAsync(
            client,
            "/v1/account/set-passphrase",
            new { passphrase = AccountTestHost.Passphrase, passphrase_confirm = AccountTestHost.Passphrase },
            bearer);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var user = await AccountTestHost.LoadUserAsync(factory, userId);
        user.KdfSalt.Should().NotBeNull().And.HaveCount(16, "fresh 16-byte Argon2id salt");
        user.DekWrapped.Should().NotBeNull().And.HaveCount(48,
            "32-byte DEK ciphertext + 16-byte GCM tag (Q-E6)");
        user.DekNonce.Should().NotBeNull().And.HaveCount(12, "AES-GCM nonce");
        user.KdfParams.Should().NotBeNull();
        using var kdf = user.KdfParams!;
        kdf.RootElement.GetProperty("memory_kb").GetInt32().Should().Be(65536);
        kdf.RootElement.GetProperty("iterations").GetInt32().Should().Be(3);
        kdf.RootElement.GetProperty("parallelism").GetInt32().Should().Be(4);
        kdf.RootElement.GetProperty("hash_length").GetInt32().Should().Be(32);

        // The wrap round-trips: derive the KEK from the stored columns and
        // unwrap (service-level sanity; M1T9 asserts byte-equality with the
        // fresh DEK).
        using var scope = factory.Services.CreateScope();
        var encryption = scope.ServiceProvider.GetRequiredService<IEncryptionService>();
        var storedKdf = kdf.RootElement.Deserialize<KdfParams>()
            ?? throw new InvalidOperationException("stored kdf_params must parse to KdfParams");
        var dek = encryption.UnwrapDek(
            userId,
            AccountTestHost.Passphrase,
            user.KdfSalt!,
            storedKdf,
            user.DekWrapped!,
            user.DekNonce!);
        dek.Should().HaveCount(32);

        var rows = await AccountTestHost.AuditRowsAsync(factory, userId);
        rows.Should().ContainSingle(a => a.Action == AuditActions.SetPassphrase)
            .Which.ActorUserId.Should().Be(userId);
    }

    [Fact]
    public async Task SetPassphrase_AlreadySet_Returns409_ProblemDetails()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        const string email = "set-passphrase-already-set@example.test";
        var (_, bearer) = await SignInAsync(factory, client, email);

        var first = await AccountTestHost.PostJsonAsync(
            client,
            "/v1/account/set-passphrase",
            new { passphrase = AccountTestHost.Passphrase, passphrase_confirm = AccountTestHost.Passphrase },
            bearer);
        first.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var second = await AccountTestHost.PostJsonAsync(
            client,
            "/v1/account/set-passphrase",
            new { passphrase = AccountTestHost.Passphrase, passphrase_confirm = AccountTestHost.Passphrase },
            bearer);

        var problem = await AccountTestHost.ReadProblemAsync(second, HttpStatusCode.Conflict);
        problem.GetProperty("type").GetString().Should().Be("/errors/passphrase-already-set");
        problem.GetProperty("detail").GetString().Should().Contain("/user/account/change-passphrase",
            "the re-wrap endpoint (M6a, Q-E3) is named in the detail");
    }

    [Fact]
    public async Task SetPassphrase_MismatchedConfirmation_Returns400_WithErrors()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        const string email = "set-passphrase-mismatch@example.test";
        var (userId, bearer) = await SignInAsync(factory, client, email);

        var response = await AccountTestHost.PostJsonAsync(
            client,
            "/v1/account/set-passphrase",
            new { passphrase = AccountTestHost.Passphrase, passphrase_confirm = "totally different phrase" },
            bearer);

        var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.BadRequest);
        problem.GetProperty("type").GetString().Should().Be("/errors/validation");
        problem.GetProperty("errors").GetArrayLength().Should().BeGreaterThan(0);
        problem.GetProperty("errors").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString())
            .Should().Contain("passphrase_confirm");

        var user = await AccountTestHost.LoadUserAsync(factory, userId);
        user.DekWrapped.Should().BeNull("a rejected set-passphrase writes no key material");
    }

    [Fact]
    public async Task SetPassphrase_TooShort_Returns400_WithErrors()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        const string email = "set-passphrase-too-short@example.test";
        var (_, bearer) = await SignInAsync(factory, client, email);

        var response = await AccountTestHost.PostJsonAsync(
            client,
            "/v1/account/set-passphrase",
            new { passphrase = "short", passphrase_confirm = "short" },
            bearer);

        var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.BadRequest);
        problem.GetProperty("type").GetString().Should().Be("/errors/validation");
        problem.GetProperty("errors").GetArrayLength().Should().BeGreaterThan(0);
        problem.GetProperty("errors").EnumerateArray()
            .Select(e => e.GetProperty("reason").GetString())
            .Should().Contain(r => r!.Contains("12", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetPassphrase_ConcurrentCallsForSameUser_OneSucceeds_OtherReturns429()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        const string email = "set-passphrase-concurrent@example.test";
        var (userId, bearer) = await SignInAsync(factory, client, email);

        // Q-E5: the per-user zero-wait mutex — both requests pass the
        // first-time check, then exactly one derivation runs.
        var payload = new { passphrase = AccountTestHost.Passphrase, passphrase_confirm = AccountTestHost.Passphrase };
        var firstTask = AccountTestHost.PostJsonAsync(
            client, "/v1/account/set-passphrase", payload, bearer);
        var secondTask = AccountTestHost.PostJsonAsync(
            client, "/v1/account/set-passphrase", payload, bearer);
        var responses = await Task.WhenAll(firstTask, secondTask);

        responses.Select(r => r.StatusCode).OrderBy(s => s)
            .Should().Equal(HttpStatusCode.NoContent, HttpStatusCode.TooManyRequests);

        var rejected = responses.Single(r => r.StatusCode == HttpStatusCode.TooManyRequests);
        var problem = await AccountTestHost.ReadProblemAsync(rejected, HttpStatusCode.TooManyRequests);
        problem.GetProperty("type").GetString().Should().Be("/errors/rate-limited");
        rejected.Headers.RetryAfter.Should().NotBeNull(
            "the SPA renders a Retry-After countdown for KEK 429s (§13.14 / Q-E5)");

        var user = await AccountTestHost.LoadUserAsync(factory, userId);
        user.DekWrapped.Should().NotBeNull("the winning call persisted the wrap");
    }
}

// ---------------------------------------------------------------------------
// POST /v1/account/change-password
// ---------------------------------------------------------------------------

[Collection("Postgres")]
public class ChangePasswordEndpointTests(PostgresFixture postgres)
{
    [Fact]
    public async Task ChangePassword_FirstLogin_OnlyNewPassword_UpdatesHash_AndFlipsMustChangePassword()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        const string email = "change-password-first-login@example.test";
        var userId = await AccountTestHost.SeedUserAsync(
            factory, email, mustChangePassword: true);
        var tempHash = (await AccountTestHost.LoadUserAsync(factory, userId)).PasswordHash;
        var bearer = await AccountTestHost.LoginAsync(client, email);

        var response = await AccountTestHost.PostJsonAsync(
            client,
            "/v1/account/change-password",
            new { new_password = AccountTestHost.NewPassword, new_password_confirm = AccountTestHost.NewPassword },
            bearer);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "no old_password is required or accepted in the first-login flow");

        var after = await AccountTestHost.LoadUserAsync(factory, userId);
        after.PasswordHash.Should().NotBe(tempHash);
        after.MustChangePassword.Should().BeFalse();

        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        (await userManager.CheckPasswordAsync(after, AccountTestHost.NewPassword)).Should().BeTrue();
        (await userManager.CheckPasswordAsync(after, AccountTestHost.TempPassword)).Should().BeFalse();

        var rows = await AccountTestHost.AuditRowsAsync(factory, userId);
        rows.Should().ContainSingle(a => a.Action == AuditActions.ChangePassword)
            .Which.MetadataJson.Should().Contain("first_login")
            .And.Contain("true");
    }

    [Fact]
    public async Task ChangePassword_Subsequent_RequiresOldPassword_AndUpdatesHash()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        const string email = "change-password-subsequent@example.test";
        var userId = await AccountTestHost.SeedUserAsync(
            factory, email, mustChangePassword: false);
        var bearer = await AccountTestHost.LoginAsync(client, email);

        // Without old_password the subsequent shape is a validation failure.
        var missingOld = await AccountTestHost.PostJsonAsync(
            client,
            "/v1/account/change-password",
            new { new_password = AccountTestHost.NewPassword, new_password_confirm = AccountTestHost.NewPassword },
            bearer);
        var missingProblem = await AccountTestHost.ReadProblemAsync(
            missingOld, HttpStatusCode.BadRequest);
        missingProblem.GetProperty("errors").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString())
            .Should().Contain("old_password");

        // With old_password + confirm the hash rotates.
        var response = await AccountTestHost.PostJsonAsync(
            client,
            "/v1/account/change-password",
            new
            {
                old_password = AccountTestHost.TempPassword,
                new_password = AccountTestHost.NewPassword,
                new_password_confirm = AccountTestHost.NewPassword,
            },
            bearer);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = await AccountTestHost.LoadUserAsync(factory, userId);
        after.MustChangePassword.Should().BeFalse();

        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        (await userManager.CheckPasswordAsync(after, AccountTestHost.NewPassword)).Should().BeTrue();
        (await userManager.CheckPasswordAsync(after, AccountTestHost.TempPassword)).Should().BeFalse();

        var rows = await AccountTestHost.AuditRowsAsync(factory, userId);
        rows.Should().ContainSingle(a => a.Action == AuditActions.ChangePassword);
    }

    [Fact]
    public async Task ChangePassword_WrongOldPassword_Returns400_WithErrors()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        const string email = "change-password-wrong-old@example.test";
        var userId = await AccountTestHost.SeedUserAsync(
            factory, email, mustChangePassword: false);
        var bearer = await AccountTestHost.LoginAsync(client, email);

        var response = await AccountTestHost.PostJsonAsync(
            client,
            "/v1/account/change-password",
            new
            {
                old_password = AccountTestHost.WrongPassword,
                new_password = AccountTestHost.NewPassword,
                new_password_confirm = AccountTestHost.NewPassword,
            },
            bearer);

        var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.BadRequest);
        problem.GetProperty("type").GetString().Should().Be("/errors/validation");
        problem.GetProperty("errors").GetArrayLength().Should().BeGreaterThan(0);

        var after = await AccountTestHost.LoadUserAsync(factory, userId);
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        (await userManager.CheckPasswordAsync(after, AccountTestHost.TempPassword)).Should().BeTrue(
            "a refused change leaves the old password intact");
    }

    [Fact]
    public async Task ChangePassword_WithoutToken_Returns401_RFC7807()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        var response = await client.PostAsync("/v1/account/change-password", null);

        var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.Unauthorized);
        problem.GetProperty("type").GetString().Should().Be("/errors/unauthorized");
        response.Headers.Contains("X-RateLimit-Limit").Should().BeFalse(
            "§13.14: the challenge response is unauthenticated");
    }
}

// ---------------------------------------------------------------------------
// POST /v1/account/refresh
// ---------------------------------------------------------------------------

[Collection("Postgres")]
public class RefreshEndpointTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Refresh_HappyPath_RotatesCookie_AndReturnsNewToken()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        const string email = "refresh-happy@example.test";
        var userId = await AccountTestHost.SeedUserAsync(factory, email);

        var login = await AccountTestHost.PostLoginAsync(
            client, email, AccountTestHost.TempPassword);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var cookie1 = AccountTestHost.RefreshCookieFrom(login);
        var loginBody = await AccountTestHost.ReadJsonAsync(login);
        var loginToken = loginBody.GetProperty("access_token").GetString()!;

        var response = await client.SendAsync(AccountTestHost.RefreshRequest(cookie1));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await AccountTestHost.ReadJsonAsync(response);
        var refreshToken = body.GetProperty("access_token").GetString();
        refreshToken.Should().NotBeNullOrWhiteSpace();
        refreshToken.Should().NotBe(loginToken, "a fresh JWT is issued per rotation");
        body.TryGetProperty("must_complete_first_login", out _).Should().BeFalse(
            "the refresh response shape is { access_token, expires_in } only (task step 8)");
        body.GetProperty("expires_in").GetInt32().Should().Be(AccountTestHost.AccessTokenTtlSeconds);

        var cookie2 = AccountTestHost.RefreshCookieFrom(response);
        cookie2.Should().NotBe(cookie1, "the refresh cookie rotates");
        AccountTestHost.AssertRefreshCookieAttributes(response);

        var rows = await AccountTestHost.AuditRowsAsync(factory, userId);
        rows.Should().ContainSingle(a => a.Action == AuditActions.RefreshSuccess);
    }

    [Fact]
    public async Task Refresh_ReuseOfRotatedToken_Returns401_AndEmitsAudit_AndInvalidatesFamily()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        const string email = "refresh-reuse@example.test";
        var userId = await AccountTestHost.SeedUserAsync(factory, email);

        var login = await AccountTestHost.PostLoginAsync(
            client, email, AccountTestHost.TempPassword);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var cookie1 = AccountTestHost.RefreshCookieFrom(login);

        var firstRotation = await client.SendAsync(AccountTestHost.RefreshRequest(cookie1));
        firstRotation.StatusCode.Should().Be(HttpStatusCode.OK);

        // Re-presenting the consumed cookie is reuse (§13.11 / Q-API5).
        var reuse = await client.SendAsync(AccountTestHost.RefreshRequest(cookie1));

        var problem = await AccountTestHost.ReadProblemAsync(reuse, HttpStatusCode.Unauthorized);
        problem.GetProperty("type").GetString().Should().Be("/errors/unauthorized");
        AccountTestHost.AssertCookieCleared(reuse);

        var rows = await AccountTestHost.AuditRowsAsync(factory, userId);
        rows.Should().ContainSingle(a => a.Action == AuditActions.RefreshSuccess,
            "only the successful rotation audits refresh_success — reuse is the store's row");
        rows.Should().Contain(a => a.Action == AuditActions.RefreshReuseDetected,
            "the store writes the reuse evidence row (M1T4)");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var family = await db.RefreshTokens.AsNoTracking()
            .Where(r => r.UserId == userId)
            .ToListAsync();
        family.Should().NotBeEmpty();
        family.Should().OnlyContain(r => r.RevokedAt != null,
            "reuse revokes every token in the family_id partition");
    }

    [Fact]
    public async Task Refresh_BadOrigin_Returns403()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        var request = AccountTestHost.RefreshRequest("irrelevant-cookie");
        request.Headers.Add("Origin", "https://evil.example");

        var response = await client.SendAsync(request);

        var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.Forbidden);
        problem.GetProperty("type").GetString().Should().Be("/errors/forbidden");
    }

    [Fact]
    public async Task Refresh_DisabledAccount_PresentationOfConsumedToken_StillEmitsReuseEvidence()
    {
        // Review/verify round-4 suggestion: the pre-rotation account gate must
        // not swallow the store's evidence path — replaying a consumed token
        // for a Disabled account still revokes the family and writes
        // refresh_reuse_detected (401), it just never mints a successor.
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        const string email = "refresh-disabled-reuse@example.test";
        var userId = await AccountTestHost.SeedUserAsync(
            factory, email, status: UserStatus.Disabled);

        string consumedCookie;
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IRefreshTokenStore>();
            var first = await store.IssueAsync(
                userId, "m1t6-disabled-reuse", "203.0.113.62", CancellationToken.None);
            _ = await store.RotateAsync(first.Record, CancellationToken.None);
            consumedCookie = first.RawToken;
        }

        var reuse = await client.SendAsync(AccountTestHost.RefreshRequest(consumedCookie));

        var problem = await AccountTestHost.ReadProblemAsync(reuse, HttpStatusCode.Unauthorized);
        problem.GetProperty("type").GetString().Should().Be("/errors/unauthorized");
        AccountTestHost.AssertCookieCleared(reuse);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
            var family = await db.RefreshTokens.AsNoTracking()
                .Where(r => r.UserId == userId)
                .ToListAsync();
            family.Should().NotBeEmpty();
            family.Should().OnlyContain(
                r => r.RevokedAt != null,
                "reuse evidence runs even while the account is disabled");
        }

        var rows = await AccountTestHost.AuditRowsAsync(factory, userId);
        rows.Should().Contain(a => a.Action == AuditActions.RefreshReuseDetected);
    }
}

// ---------------------------------------------------------------------------
// POST /v1/account/logout
// ---------------------------------------------------------------------------

[Collection("Postgres")]
public class LogoutEndpointTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Logout_RevokesToken_AndClearsCookie()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        const string email = "logout-happy@example.test";
        var userId = await AccountTestHost.SeedUserAsync(factory, email);

        var login = await AccountTestHost.PostLoginAsync(
            client, email, AccountTestHost.TempPassword);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var cookie = AccountTestHost.RefreshCookieFrom(login);

        var response = await client.SendAsync(AccountTestHost.LogoutRequest(cookie));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        AccountTestHost.AssertCookieCleared(response);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
            var tokens = await db.RefreshTokens.AsNoTracking()
                .Where(r => r.UserId == userId)
                .ToListAsync();
            tokens.Should().NotBeEmpty();
            tokens.Should().OnlyContain(r => r.RevokedAt != null,
                "logout marks the session token revoked");
        }

        var rows = await AccountTestHost.AuditRowsAsync(factory, userId);
        rows.Should().ContainSingle(a => a.Action == AuditActions.Logout)
            .Which.ActorUserId.Should().Be(userId);

        // Milestone exit criterion: the revoked cookie can no longer refresh.
        var afterLogout = await client.SendAsync(AccountTestHost.RefreshRequest(cookie));
        await AccountTestHost.ReadProblemAsync(afterLogout, HttpStatusCode.Unauthorized);

        // Idempotent: a cookie-less logout is still 204.
        var anonymous = await client.PostAsync("/v1/account/logout", null);
        anonymous.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}

// ---------------------------------------------------------------------------
// Cross-cutting ACs: §13.14 headers, §15 audit rows, §13.12 5xx shape.
// ---------------------------------------------------------------------------

[Collection("Postgres")]
public class AccountCrossCuttingTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Authenticated_Endpoint_EmitsXRateLimitHeaders_OnEveryResponse()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        const string email = "x-rate-limit-headers@example.test";
        await AccountTestHost.SeedUserAsync(factory, email, mustChangePassword: true);
        var bearer = await AccountTestHost.LoginAsync(client, email);

        // First authenticated response: full header set, budget 60/min (Q-O7).
        var success = await AccountTestHost.PostJsonAsync(
            client,
            "/v1/account/set-passphrase",
            new { passphrase = AccountTestHost.Passphrase, passphrase_confirm = AccountTestHost.Passphrase },
            bearer);
        success.StatusCode.Should().Be(HttpStatusCode.NoContent);
        success.Headers.GetValues("X-RateLimit-Limit").Single().Should().Be("60");
        success.Headers.GetValues("X-RateLimit-Remaining").Single().Should().Be("59");
        long.TryParse(success.Headers.GetValues("X-RateLimit-Reset").Single(), out var reset)
            .Should().BeTrue("X-RateLimit-Reset is unix seconds");
        reset.Should().BeGreaterThan(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // A 4xx from an authenticated endpoint carries them too ("every
        // authenticated response"); exact remaining is asserted on the first
        // response, the second only needs the full set within budget (the
        // fixed window may roll over mid-test).
        var failure = await AccountTestHost.PostJsonAsync(
            client,
            "/v1/account/change-password",
            new { new_password = AccountTestHost.NewPassword },
            bearer);
        failure.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        failure.Headers.GetValues("X-RateLimit-Limit").Single().Should().Be("60");
        long.Parse(failure.Headers.GetValues("X-RateLimit-Remaining").Single())
            .Should().BeLessThanOrEqualTo(59);
        failure.Headers.Contains("X-RateLimit-Reset").Should().BeTrue();
    }

    [Fact]
    public async Task Audit_RowsWritten_ForAuthEvents()
    {
        // Milestone exit criterion: act out login → otp-login →
        // change-password → set-passphrase → logout and require one row per
        // event with non-null actor_user_id, ip and hmac.
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        const string email = "audit-auth-flow@example.test";
        var userId = await AccountTestHost.SeedUserAsync(
            factory, email, mustChangePassword: true);

        var login = await AccountTestHost.PostLoginAsync(
            client, email, AccountTestHost.TempPassword);
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        var otp = await AccountTestHost.IssueOtpAsync(factory, userId);
        var otpLogin = await AccountTestHost.PostOtpLoginAsync(client, email, otp);
        otpLogin.StatusCode.Should().Be(HttpStatusCode.OK);
        var otpBody = await AccountTestHost.ReadJsonAsync(otpLogin);
        var bearer = otpBody.GetProperty("access_token").GetString()!;
        var cookie = AccountTestHost.RefreshCookieFrom(otpLogin);

        var change = await AccountTestHost.PostJsonAsync(
            client,
            "/v1/account/change-password",
            new { new_password = AccountTestHost.NewPassword, new_password_confirm = AccountTestHost.NewPassword },
            bearer);
        change.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var setPassphrase = await AccountTestHost.PostJsonAsync(
            client,
            "/v1/account/set-passphrase",
            new { passphrase = AccountTestHost.Passphrase, passphrase_confirm = AccountTestHost.Passphrase },
            bearer);
        setPassphrase.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var logout = await client.SendAsync(AccountTestHost.LogoutRequest(cookie));
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var rows = await AccountTestHost.AuditRowsAsync(factory, userId);
        var actions = rows.Select(r => r.Action).ToList();
        actions.Should().Contain([
            AuditActions.LoginSuccess,
            AuditActions.OtpLoginSuccess,
            AuditActions.ChangePassword,
            AuditActions.SetPassphrase,
            AuditActions.Logout
        ]);

        rows.Where(r => actions.Contains(r.Action)).Should().OnlyContain(r =>
            r.ActorUserId == userId
            && r.Ip != null
            && r.Hmac != null
            && r.Hmac.Length == 32,
            "every auth-event row carries actor_user_id, ip and a full HMAC");

        using var scope = factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
        (await audit.VerifyChainAsync(CancellationToken.None)).Should().BeNull(
            "the HMAC chain stays intact after the flow");
    }

    [Fact]
    public async Task UnhandledException_Returns500_RFC7807()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        var response = await client.GetAsync("/__m1t6/throw");

        var problem = await AccountTestHost.ReadProblemAsync(
            response, HttpStatusCode.InternalServerError);
        problem.GetProperty("type").GetString().Should().Be("/errors/server-error");
        problem.GetProperty("instance").GetString().Should().Be("/__m1t6/throw");
    }

    [Fact]
    public async Task MalformedJsonBody_Returns400_RFC7807()
    {
        // AC: even a framework-level parse failure surfaces as a §13.12
        // document (the endpoints read the body manually for this reason).
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        const string email = "malformed-json-body@example.test";
        await AccountTestHost.SeedUserAsync(factory, email, mustChangePassword: true);
        var bearer = await AccountTestHost.LoginAsync(client, email);

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/account/change-password")
        {
            Content = new StringContent("{not valid json", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        var response = await client.SendAsync(request);

        var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.BadRequest);
        problem.GetProperty("type").GetString().Should().Be("/errors/validation");
        problem.GetProperty("errors").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task UnknownRoute_Returns404_RFC7807()
    {
        // AC: unmatched routes are problem documents too — a dotted path is
        // used deliberately, since MapFallback's {*path:nonfile} pattern
        // would leave it an empty-bodied 404.
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        var response = await client.GetAsync("/v1/no-such-route.json");

        var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.NotFound);
        problem.GetProperty("type").GetString().Should().Be("/errors/not-found");
        problem.GetProperty("instance").GetString().Should().Be("/v1/no-such-route.json");
    }

    [Fact]
    public async Task LoginForm_WithoutFormContentType_Returns400_RFC7807()
    {
        // Review finding 2: a non-form body on the login endpoint is a client
        // error (400 problem), never a 500 from ReadFormAsync.
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        var response = await client.PostAsync(
            "/v1/account/login",
            new StringContent("{\"email\":\"not-a-form@example.test\"}", Encoding.UTF8, "application/json"));

        var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.BadRequest);
        problem.GetProperty("type").GetString().Should().Be("/errors/validation");
        problem.GetProperty("errors").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task DisabledAccount_IsRejected_OnLoginRefreshAndAuthenticatedEndpoints()
    {
        // Review WARNING: status=Disabled cuts access everywhere — password
        // login (after credential verification, so no status oracle), a live
        // refresh token (without rotating it), and a still-valid JWT on the
        // first-login bootstrap endpoints.
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        const string email = "disabled-account@example.test";
        var userId = await AccountTestHost.SeedUserAsync(
            factory, email, status: UserStatus.Disabled, mustChangePassword: true);

        // 1) Password login → 403 with the reason in the audited failure row.
        var login = await AccountTestHost.PostLoginAsync(client, email, AccountTestHost.TempPassword);
        var loginProblem = await AccountTestHost.ReadProblemAsync(login, HttpStatusCode.Forbidden);
        loginProblem.GetProperty("type").GetString().Should().Be("/errors/forbidden");

        var rows = await AccountTestHost.AuditRowsAsync(factory, userId);
        rows.Should().ContainSingle(a => a.Action == AuditActions.LoginFailure)
            .Which.MetadataJson.Should().Contain("account_disabled");

        // 2) Refresh with a store-issued live token → 403, cookie cleared,
        //    and the token NOT rotated (account lookup precedes rotation).
        string rawCookie;
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IRefreshTokenStore>();
            var issued = await store.IssueAsync(
                userId, "m1t6-disabled-test", "203.0.113.61", CancellationToken.None);
            rawCookie = issued.RawToken;
        }

        var refresh = await client.SendAsync(AccountTestHost.RefreshRequest(rawCookie));
        var refreshProblem = await AccountTestHost.ReadProblemAsync(refresh, HttpStatusCode.Forbidden);
        refreshProblem.GetProperty("type").GetString().Should().Be("/errors/forbidden");
        AccountTestHost.AssertCookieCleared(refresh);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
            var tokenRow = await db.RefreshTokens.AsNoTracking()
                .Where(r => r.UserId == userId)
                .SingleAsync();
            tokenRow.UsedAt.Should().BeNull("no rotation may happen for a disabled account");
        }

        // 3) A directly issued (cryptographically valid) JWT is still refused
        //    by the authenticated bootstrap endpoints.
        string bearer;
        using (var scope = factory.Services.CreateScope())
        {
            var issuer = scope.ServiceProvider.GetRequiredService<IJwtTokenIssuer>();
            var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
            var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId);
            bearer = issuer.IssueAccessToken(user, []);
        }

        var setPassphrase = await AccountTestHost.PostJsonAsync(
            client,
            "/v1/account/set-passphrase",
            new { passphrase = AccountTestHost.Passphrase, passphrase_confirm = AccountTestHost.Passphrase },
            bearer);
        (await AccountTestHost.ReadProblemAsync(setPassphrase, HttpStatusCode.Forbidden))
            .GetProperty("type").GetString().Should().Be("/errors/forbidden");

        var changePassword = await AccountTestHost.PostJsonAsync(
            client,
            "/v1/account/change-password",
            new { new_password = AccountTestHost.NewPassword, new_password_confirm = AccountTestHost.NewPassword },
            bearer);
        (await AccountTestHost.ReadProblemAsync(changePassword, HttpStatusCode.Forbidden))
            .GetProperty("type").GetString().Should().Be("/errors/forbidden");
    }
}
