using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using EmailAgent.Api.Auth;
using EmailAgent.Core.Audit;
using EmailAgent.Core.Errors;
using EmailAgent.Core.Identity;
using EmailAgent.Core.Options;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace EmailAgent.IntegrationTests.Auth;

/// <summary>
/// M1T4 task step 11: integration coverage for the auth plumbing against
/// Testcontainers Postgres + <see cref="WebApplicationFactory{TEntryPoint}"/>.
/// Account endpoints (login/refresh/logout HTTP surface) are M1T6's; the
/// "full refresh-rotation cycle" AC is exercised here at the service layer
/// (real store + real issuer + real audit chain on a real database), and the
/// HTTP-surface ACs that already exist in M1T4 (Origin gate, rate-limit
/// headers/policies) are exercised over the wire.
/// </summary>
[Collection("Postgres")]
public class AuthSmokeTests(PostgresFixture postgres)
{
    /// <summary>Ephemeral test key — never a real secret (AGENTS.md).</summary>
    private const string TestSigningKey = "m1t4-integration-test-signing-key-0123456789";

    private static readonly SemaphoreSlim MigrationGate = new(1, 1);
    private static string? _migratedConnectionString;

    [Fact]
    public async Task Jwt_Issue_Then_Validate_Succeeds_With_Correct_Issuer_Audience()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var issuer = scope.ServiceProvider.GetRequiredService<IJwtTokenIssuer>();

        var userId = Guid.NewGuid();
        var user = NewUser(userId, "jwt-issue@example.test");
        var token = issuer.IssueAccessToken(user, ["User"]);

        token.Should().NotBeNullOrWhiteSpace();

        var parameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSigningKey)),
            ValidateIssuer = true,
            ValidIssuer = "email-agent",
            ValidateAudience = true,
            ValidAudience = "email-agent-spa",
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        };

        var handler = new JwtSecurityTokenHandler();
        var principal = handler.ValidateToken(token, parameters, out var tokenResult);
        var validated = (JwtSecurityToken)tokenResult;

        validated.Issuer.Should().Be("email-agent");
        validated.Audiences.Should().Contain("email-agent-spa");
        principal.FindFirst("role")?.Value.Should().Be("User");
        validated.Claims
            .Single(c => c.Type == JwtRegisteredClaimNames.Sub)
            .Value.Should().Be(userId.ToString());
    }

    [Fact]
    public async Task Refresh_Rotation_HappyPath_Issues_New_AccessToken_And_Cookie()
    {
        var connectionString = await MigrateAsync();
        await using var factory = CreateFactory(connectionString);
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRefreshTokenStore>();
        var issuer = scope.ServiceProvider.GetRequiredService<IJwtTokenIssuer>();

        var userId = await SeedUserAsync(factory, "rotate-happy@example.test");
        var first = await store.IssueAsync(userId, "m1t4-test-agent", "203.0.113.10", CancellationToken.None);
        var second = await store.RotateAsync(first.Record, CancellationToken.None);

        second.Record.FamilyId.Should().Be(first.Record.FamilyId, "rotation stays in the same family");
        second.Record.Id.Should().NotBe(first.Record.Id);
        second.RawToken.Should().NotBe(first.RawToken);
        second.Record.UsedAt.Should().BeNull();

        var old = await store.FindByHashAsync(first.Record.TokenHash, CancellationToken.None);
        old!.UsedAt.Should().NotBeNull("the consumed row is marked used");
        old.ReplacedById.Should().Be(second.Record.Id, "the chain links old → new");

        // SQ-4 cookie attributes on the rotated token.
        var http = new DefaultHttpContext();
        RefreshTokenCookieWriter.Write(http.Response, second.RawToken, second.Record.ExpiresAt);
        var setCookie = http.Response.Headers.SetCookie.ToString();
        setCookie.Should().MatchRegex("(?i)ea_refresh=");
        setCookie.Should().MatchRegex("(?i)path=/v1/account/refresh");
        setCookie.Should().MatchRegex("(?i)httponly");
        setCookie.Should().MatchRegex("(?i)secure");
        setCookie.Should().MatchRegex("(?i)samesite=lax");

        // A fresh access token can be minted for the same user after rotation.
        var user = await LoadUserAsync(factory, userId);
        var accessToken = issuer.IssueAccessToken(user, ["User"]);
        accessToken.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Refresh_Reuse_Of_Rotated_Token_Invalidates_Family_And_Writes_Audit()
    {
        var connectionString = await MigrateAsync();
        await using var factory = CreateFactory(connectionString);
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRefreshTokenStore>();

        var userId = await SeedUserAsync(factory, "rotate-reuse@example.test");
        var first = await store.IssueAsync(userId, "m1t4-test-agent", "203.0.113.11", CancellationToken.None);
        var second = await store.RotateAsync(first.Record, CancellationToken.None);

        // Re-presenting the consumed token is reuse (§13.11 / Q-API5 /
        // milestone exit criterion 3: refresh_reuse_detected).
        var rotateAgain = () => store.RotateAsync(first.Record, CancellationToken.None);
        await rotateAgain.Should().ThrowAsync<RefreshTokenReuseException>();

        // Verify through a fresh scope: committed state (not this scope's
        // change-tracked copies) is what a subsequent request would observe.
        using var verifyScope = factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var familyRows = await db.RefreshTokens
            .Where(r => r.FamilyId == first.Record.FamilyId)
            .ToListAsync();
        familyRows.Should().NotBeEmpty();
        familyRows.Should().OnlyContain(
            r => r.RevokedAt != null,
            "reuse revokes every token in the family_id partition");

        var verifyStore = verifyScope.ServiceProvider.GetRequiredService<IRefreshTokenStore>();
        var successor = await verifyStore.FindByHashAsync(second.Record.TokenHash, CancellationToken.None);
        successor!.RevokedAt.Should().NotBeNull("a live session cannot outlive reuse");

        var auditRows = await db.AuditLog
            .Where(a => a.Action == AuditActions.RefreshReuseDetected)
            .Where(a => a.TargetId == first.Record.Id.ToString())
            .ToListAsync();
        auditRows.Should().NotBeEmpty("the reuse evidence row is written");
        auditRows.Should().OnlyContain(a => a.Hmac != null && a.Hmac.Length == 32);
        auditRows.Should().OnlyContain(a => a.Ip != null, "reasonable ip value");

        // AC: chain verifier is clean after the reuse write.
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
        (await audit.VerifyChainAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Logout_Invalidates_Refresh_Cookie_And_Disables_Further_Refresh()
    {
        var connectionString = await MigrateAsync();
        await using var factory = CreateFactory(connectionString);
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRefreshTokenStore>();

        var userId = await SeedUserAsync(factory, "logout@example.test");
        var issued = await store.IssueAsync(userId, "m1t4-test-agent", "203.0.113.12", CancellationToken.None);

        await store.RevokeAsync(issued.Record.TokenHash, CancellationToken.None);

        // Verify through a fresh scope: logout must be visible to the next
        // request (committed state), not just to this scope's tracked copy.
        using (var verifyScope = factory.Services.CreateScope())
        {
            var verifyStore = verifyScope.ServiceProvider.GetRequiredService<IRefreshTokenStore>();
            var reloaded = await verifyStore.FindByHashAsync(issued.Record.TokenHash, CancellationToken.None);
            reloaded!.RevokedAt.Should().NotBeNull("logout marks revoked_at, it does not delete the row");

            // Further refresh of the revoked token is refused (reuse path).
            var rotate = () => verifyStore.RotateAsync(issued.Record, CancellationToken.None);
            await rotate.Should().ThrowAsync<RefreshTokenReuseException>();
        }

        // The cookie is cleared (Max-Age/expires=0 on the ea_refresh cookie).
        var http = new DefaultHttpContext();
        RefreshTokenCookieWriter.Clear(http.Response);
        var setCookie = http.Response.Headers.SetCookie.ToString();
        setCookie.Should().MatchRegex("(?i)ea_refresh=");
        setCookie.Should().MatchRegex("(?i)path=/v1/account/refresh");
    }

    [Fact]
    public async Task RateLimit_Five_Logins_Per_Minute_Per_IP_Sixth_Returns_429_With_RetryAfter()
    {
        await using var factory = CreateFactory(mapTestEndpoints: true);
        using var client = factory.CreateClient();

        for (var i = 0; i < RateLimitPolicies.LoginPerMinute; i++)
        {
            var ok = await client.PostAsync("/__m1t4/login-per-ip", null);
            ok.StatusCode.Should().Be(HttpStatusCode.OK, $"attempt {i + 1} is within the 5/min budget");
        }

        var rejected = await client.PostAsync("/__m1t4/login-per-ip", null);
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the sixth attempt is rejected");
        rejected.Headers.RetryAfter.Should().NotBeNull("all three partitions emit Retry-After on 429");

        // §13.14: unauthenticated responses do not carry X-RateLimit-*.
        rejected.Headers.Contains("X-RateLimit-Limit").Should().BeFalse(
            "login responses are unauthenticated, so they must not carry X-RateLimit-*");
    }

    [Fact]
    public async Task RateLimit_Five_Logins_Per_Minute_Per_Account_Sixth_Returns_429()
    {
        await using var factory = CreateFactory(mapTestEndpoints: true);
        using var client = factory.CreateClient();

        for (var i = 0; i < RateLimitPolicies.LoginPerMinute; i++)
        {
            var ok = await client.PostAsync("/__m1t4/login-per-account", null);
            ok.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var rejected = await client.PostAsync("/__m1t4/login-per-account", null);
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        rejected.Headers.RetryAfter.Should().NotBeNull();
    }

    [Fact]
    public async Task Refresh_Path_Rejects_Origin_Not_In_AllowList()
    {
        // Spa:AllowedOrigins ships empty (same-origin default): an external
        // Origin on the refresh path is a 403 before any endpoint runs.
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/account/refresh");
        request.Headers.Add("Origin", "https://evil.example");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType?.ToString()
            .Should().Contain("application/problem+json", "RFC 7807 responses carry the problem media type");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("/errors/forbidden");
    }

    [Fact]
    public async Task Refresh_Path_Accepts_SameOrigin_OriginHeader()
    {
        // Default posture (both allow-list settings empty): same-origin
        // browser requests pass the gate — only foreign origins 403 (task
        // step 8: "empty by default → accept same-origin only").
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/account/refresh");
        var origin = $"{client.BaseAddress!.Scheme}://{client.BaseAddress.Authority}";
        request.Headers.Add("Origin", origin);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().NotBe(
            HttpStatusCode.Forbidden,
            "the same-origin request must clear the Origin gate (the resulting "
                + "404 only reflects that M1T6's refresh endpoint does not exist yet)");
    }

    [Fact]
    public async Task Authenticated_Response_Carries_XRateLimit_Headers()
    {
        // AC: X-RateLimit-Limit/-Remaining/-Reset on every authenticated
        // response (§13.14).
        await using var factory = CreateFactory(mapTestEndpoints: true);
        using var scope = factory.Services.CreateScope();
        var issuer = scope.ServiceProvider.GetRequiredService<IJwtTokenIssuer>();
        var user = NewUser(Guid.NewGuid(), "headers@example.test");
        var token = issuer.IssueAccessToken(user, ["User"]);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/__m1t4/authenticated");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        response.Headers.Contains("X-RateLimit-Limit").Should().BeTrue();
        response.Headers.GetValues("X-RateLimit-Limit").Single()
            .Should().Be(RateLimitPolicies.AuthenticatedPerMinute.ToString());
        response.Headers.Contains("X-RateLimit-Remaining").Should().BeTrue();
        response.Headers.GetValues("X-RateLimit-Remaining").Single()
            .Should().Be((RateLimitPolicies.AuthenticatedPerMinute - 1).ToString());
        response.Headers.Contains("X-RateLimit-Reset").Should().BeTrue();
        long.TryParse(
            response.Headers.GetValues("X-RateLimit-Reset").Single(),
            out var reset)
            .Should().BeTrue("reset is unix seconds");
        reset.Should().BeGreaterThan(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // An unauthenticated hit on the same endpoint must not carry them.
        using var anonymous = factory.CreateClient();
        var anonymousResponse = await anonymous.GetAsync("/__m1t4/authenticated");
        anonymousResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        anonymousResponse.Headers.Contains("X-RateLimit-Limit").Should().BeFalse(
            "§13.14: unauthenticated responses do not carry these headers");
    }

    [Fact]
    public async Task Audit_AllM1Actions_Persist_WithHmac_And_Chain_Verifies_Clean()
    {
        // AC: rows for every M1 action constant exist with non-null hmac and
        // reasonable actor/ip; the chain verifier returns null afterwards.
        var connectionString = await MigrateAsync();
        await using var factory = CreateFactory(connectionString);
        using var scope = factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();

        var userId = await SeedUserAsync(factory, "audit-actions@example.test");
        var before = await db.AuditLog.CountAsync();
        var ip = IPAddress.Parse("203.0.113.7");

        var actions = new[]
        {
            AuditActions.LoginSuccess,
            AuditActions.LoginFailure,
            AuditActions.RefreshSuccess,
            AuditActions.RefreshReuseDetected,
            AuditActions.Logout,
            AuditActions.ChangePassword,
            AuditActions.SetPassphrase,
            AuditActions.OtpLoginSuccess,
            AuditActions.OtpLoginFailure,
            AuditActions.AdminCreateUser,
            AuditActions.AdminOtpIssued,
        };

        foreach (var action in actions)
        {
            await audit.LogAsync(
                new AuditEvent(
                    ActorUserId: userId,
                    ActorRole: "User",
                    Action: action,
                    TargetType: "user",
                    TargetId: userId.ToString(),
                    MetadataJson: "{\"m1t4\":true}",
                    Ip: ip),
                CancellationToken.None);
        }

        var rows = await db.AuditLog
            .Where(a => a.Action == AuditActions.LoginSuccess || a.Action == AuditActions.LoginFailure
                || a.Action == AuditActions.RefreshSuccess || a.Action == AuditActions.RefreshReuseDetected
                || a.Action == AuditActions.Logout || a.Action == AuditActions.ChangePassword
                || a.Action == AuditActions.SetPassphrase || a.Action == AuditActions.OtpLoginSuccess
                || a.Action == AuditActions.OtpLoginFailure || a.Action == AuditActions.AdminCreateUser
                || a.Action == AuditActions.AdminOtpIssued)
            // Scope to this test's own writes: sibling tests in the shared
            // collection also emit refresh_reuse_detected (TargetId = a token
            // id), and this test's rows all target the seeded user.
            .Where(a => a.TargetId == userId.ToString())
            .ToListAsync();

        rows.Count.Should().BeGreaterThanOrEqualTo(actions.Length);
        rows.Should().OnlyContain(a => a.Hmac != null && a.Hmac.Length == 32);
        rows.Should().OnlyContain(a => a.ActorUserId == userId);
        rows.Should().OnlyContain(a => a.ActorRole == "User");
        rows.Should().OnlyContain(a => a.Ip != null);

        (await db.AuditLog.CountAsync()).Should().Be(before + actions.Length);

        // AC: the HMAC chain verifier returns null after every successful test.
        (await audit.VerifyChainAsync(CancellationToken.None)).Should().BeNull();
    }

    private static User NewUser(Guid id, string email) => new()
    {
        Id = id,
        Email = email,
        UserName = email,
        NormalizedEmail = email.ToUpperInvariant(),
        NormalizedUserName = email.ToUpperInvariant(),
        Role = UserRole.User,
        Status = UserStatus.Active,
        PasswordHash = "not-a-real-hash-integration-fixture",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<Guid> SeedUserAsync(
        WebApplicationFactory<Program> factory,
        string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var user = NewUser(Guid.NewGuid(), email);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<User> LoadUserAsync(
        WebApplicationFactory<Program> factory,
        Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return await db.Users.FirstAsync(u => u.Id == id);
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string? connectionString = null,
        bool mapTestEndpoints = false)
    {
        var factory = ApiTestFactory.Create(connectionString);
        if (!mapTestEndpoints)
        {
            return factory;
        }

        // M1T6's login endpoints do not exist yet; register the factory-only
        // routes through an IStartupFilter (ConfigureServices lands for
        // minimal-hosting apps — see TestEndpointsStartupFilter) so the real
        // named policies and the §13.14 header rule are exercised over HTTP.
        return factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddTransient<IStartupFilter, TestEndpointsStartupFilter>();
        }));
    }

    /// <summary>
    /// Applies every migration once per collection fixture (the class shares
    /// the "Postgres" collection with InitialSchemaTests, so tests run
    /// sequentially — no cross-class migration race).
    /// </summary>
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
