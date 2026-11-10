using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EmailAgent.Api.Auth;
using EmailAgent.Core.Audit;
using EmailAgent.Core.Identity;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmailAgent.IntegrationTests.Endpoints;

// ---------------------------------------------------------------------------
// M1T7 admin user-management suite. Reuses the M1T6 harness
// (AccountTestHost: shared Postgres migration gate, factory + cookie-less
// client, RFC 7807 readers) because both suites live in the same folder and
// the same "Postgres" collection. The harness difference: users are inserted
// directly through DbContext (fast, no password hashing — the task's
// "create an Admin user directly in DbContext" setup), the Identity role
// rows are seeded here (M1T4 stores roles in the Identity role tables and
// nothing else provisions them), and admin tokens are minted by the real
// IJwtTokenIssuer with an explicit role claim.
// ---------------------------------------------------------------------------

internal static class AdminTestHost
{
    /// <summary>Inserts the `Admin` / `User` Identity role rows if absent (M1T4/SQ-10).</summary>
    public static async Task EnsureRolesAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();

        foreach (var name in new[] { "Admin", "User" })
        {
            var normalized = name.ToUpperInvariant();
            if (await db.IdentityRoles.AnyAsync(r => r.NormalizedName == normalized))
            {
                continue;
            }

            db.IdentityRoles.Add(new IdentityRole<Guid>
            {
                Id = Guid.NewGuid(),
                Name = name,
                NormalizedName = normalized,
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Direct DbContext insert: no password hashing, no role link.</summary>
    public static async Task<Guid> SeedUserAsync(
        WebApplicationFactory<Program> factory,
        string email,
        UserRole role = UserRole.User,
        UserStatus status = UserStatus.Active,
        bool mustChangePassword = false)
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
            PasswordHash = "m1t7-seeded-not-a-real-login-hash",
            Role = role,
            Status = status,
            MustChangePassword = mustChangePassword,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>Seeds <paramref name="count"/> users with strictly decreasing created_at.</summary>
    public static async Task<List<Guid>> SeedManyAsync(
        WebApplicationFactory<Program> factory,
        int count,
        string prefix)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var createdAt = DateTimeOffset.UtcNow;
        var ids = new List<Guid>(count);

        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            var email = $"{prefix}-{i:D3}@example.test";
            ids.Add(id);
            db.Users.Add(new User
            {
                Id = id,
                Email = email,
                UserName = email,
                NormalizedEmail = email.ToUpperInvariant(),
                NormalizedUserName = email.ToUpperInvariant(),
                PasswordHash = "m1t7-seeded-not-a-real-login-hash",
                Role = UserRole.User,
                Status = UserStatus.Active,
                MustChangePassword = false,
                CreatedAt = createdAt.AddSeconds(-i),
            });
        }

        await db.SaveChangesAsync();
        return ids;
    }

    /// <summary>Mints a real access token with an explicit role claim (M1T4 issuer).</summary>
    public static async Task<string> BearerAsync(
        WebApplicationFactory<Program> factory,
        Guid userId,
        string role)
    {
        using var scope = factory.Services.CreateScope();
        var issuer = scope.ServiceProvider.GetRequiredService<IJwtTokenIssuer>();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        return issuer.IssueAccessToken(user, [role]);
    }

    public static HttpRequestMessage Request(
        HttpMethod method,
        string path,
        string bearer,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", bearer) },
        };

        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        return request;
    }

    /// <summary>POST /v1/admin/users (asserts 201) and returns the parsed body.</summary>
    public static async Task<JsonElement> CreateUserAsync(
        HttpClient client,
        string bearer,
        string email)
    {
        var response = await client.SendAsync(
            Request(HttpMethod.Post, "/v1/admin/users", bearer, new { email }));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return await AccountTestHost.ReadJsonAsync(response);
    }
}

// ---------------------------------------------------------------------------
// GET/POST /v1/admin/users, GET /v1/admin/users/{id}, POST …/{id}/otp
// ---------------------------------------------------------------------------

[Collection("Postgres")]
public class AdminUserEndpointsTests(PostgresFixture postgres)
{
    private static string UniqueEmail(string prefix)
        => $"m1t7-{prefix}-{Guid.NewGuid():N}@example.test";

    /// <summary>
    /// Standard per-test setup (mirrors AccountEndpointsTests): migrated
    /// shared Postgres, factory + cookie-less client, and a seeded Admin
    /// whose token is minted by the real issuer with the Admin role claim.
    /// </summary>
    private static async Task<
        (WebApplicationFactory<Program> Factory, HttpClient Client, Guid AdminId, string Bearer)>
        CreateAdminHostAsync(PostgresFixture postgres)
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        var factory = AccountTestHost.CreateFactory(connectionString);
        var client = AccountTestHost.CreateClient(factory);

        // Role rows must exist before POST /v1/admin/users can AddToRoleAsync.
        await AdminTestHost.EnsureRolesAsync(factory);

        var adminId = await AdminTestHost.SeedUserAsync(
            factory, UniqueEmail("admin"), UserRole.Admin, UserStatus.Active);
        var bearer = await AdminTestHost.BearerAsync(factory, adminId, "Admin");

        return (factory, client, adminId, bearer);
    }

    [Fact]
    public async Task List_RequiresAdminRole_NonAdminGets403()
    {
        var (factory, client, adminId, adminBearer) = await CreateAdminHostAsync(postgres);
        await using var disposableFactory = factory;
        using var httpClient = client;

        // Anonymous → 401 RFC 7807 (JwtBearer challenge event).
        var anonymous = await httpClient.GetAsync("/v1/admin/users");
        var unauthorized = await AccountTestHost.ReadProblemAsync(
            anonymous, HttpStatusCode.Unauthorized);
        unauthorized.GetProperty("type").GetString().Should().Be("/errors/unauthorized");

        // Authenticated non-admin → 403 RFC 7807 (JwtBearer forbid event) on
        // both a read and a write route (AC: blocks every non-admin caller).
        var userId = await AdminTestHost.SeedUserAsync(
            factory, UniqueEmail("non-admin"), UserRole.User);
        var userBearer = await AdminTestHost.BearerAsync(factory, userId, "User");

        var forbidden = await httpClient.SendAsync(
            AdminTestHost.Request(HttpMethod.Get, "/v1/admin/users", userBearer));
        (await AccountTestHost.ReadProblemAsync(forbidden, HttpStatusCode.Forbidden))
            .GetProperty("type").GetString().Should().Be("/errors/forbidden");

        var forbiddenPost = await httpClient.SendAsync(
            AdminTestHost.Request(
                HttpMethod.Post, "/v1/admin/users", userBearer,
                new { email = UniqueEmail("blocked") }));
        (await AccountTestHost.ReadProblemAsync(forbiddenPost, HttpStatusCode.Forbidden))
            .GetProperty("type").GetString().Should().Be("/errors/forbidden");

        // …while an Admin caller walks right through (role plumbing proof).
        adminId.Should().NotBeEmpty();
        var allowed = await httpClient.SendAsync(
            AdminTestHost.Request(HttpMethod.Get, "/v1/admin/users", adminBearer));
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DisabledAdmin_Returns403_OnAllFourEndpoints()
    {
        // Review WARNING: a still-valid JWT must not keep admin powers for a
        // Disabled account — least of all OTP reissue, which mints a fresh
        // login credential. Parity with the M1T6 account-endpoint gate.
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var httpClient = AccountTestHost.CreateClient(factory);
        await AdminTestHost.EnsureRolesAsync(factory);

        var disabledAdminId = await AdminTestHost.SeedUserAsync(
            factory, UniqueEmail("disabled-admin"), UserRole.Admin, UserStatus.Disabled);
        var bearer = await AdminTestHost.BearerAsync(factory, disabledAdminId, "Admin");
        var targetId = await AdminTestHost.SeedUserAsync(
            factory, UniqueEmail("disabled-target"));
        var createEmail = UniqueEmail("disabled-create");

        var requests = new[]
        {
            AdminTestHost.Request(HttpMethod.Get, "/v1/admin/users", bearer),
            AdminTestHost.Request(
                HttpMethod.Post, "/v1/admin/users", bearer, new { email = createEmail }),
            AdminTestHost.Request(HttpMethod.Get, $"/v1/admin/users/{targetId}", bearer),
            AdminTestHost.Request(HttpMethod.Post, $"/v1/admin/users/{targetId}/otp", bearer),
        };

        foreach (var request in requests)
        {
            var response = await httpClient.SendAsync(request);
            var problem = await AccountTestHost.ReadProblemAsync(
                response, HttpStatusCode.Forbidden);
            problem.GetProperty("type").GetString().Should().Be("/errors/forbidden");
        }

        // The blocked create wrote nothing.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        (await db.Users.AsNoTracking().AnyAsync(user => user.Email == createEmail))
            .Should().BeFalse("no user may be created by a disabled actor");
    }

    [Fact]
    public async Task List_DefaultLimit_25_And_ReturnsPagedShape()
    {
        var (factory, client, _, bearer) = await CreateAdminHostAsync(postgres);
        await using var disposableFactory = factory;
        using var httpClient = client;

        // 30 distinct created_at values ⇒ more than the default page.
        await AdminTestHost.SeedManyAsync(factory, 30, "m1t7-default-limit");

        var response = await httpClient.SendAsync(
            AdminTestHost.Request(HttpMethod.Get, "/v1/admin/users", bearer));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // §13.14: authenticated responses carry the rate-limit headers.
        response.Headers.Contains("X-RateLimit-Limit").Should().BeTrue();
        response.Headers.Contains("X-RateLimit-Remaining").Should().BeTrue();
        response.Headers.Contains("X-RateLimit-Reset").Should().BeTrue();

        var body = await AccountTestHost.ReadJsonAsync(response);
        body.GetProperty("items").GetArrayLength().Should().Be(25, "§13.13 default limit is 25");
        body.GetProperty("next_cursor").GetString().Should().NotBeNullOrWhiteSpace(
            "more than one page exists");

        // Exact wire shape: the seven non-sensitive members, nothing else.
        var item = body.GetProperty("items")[0];
        item.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(new[]
        {
            "id", "email", "role", "status", "must_change_password",
            "created_at", "last_login_at",
        });
    }

    [Fact]
    public async Task List_Cursor_Advances_AcrossPages()
    {
        var (factory, client, _, bearer) = await CreateAdminHostAsync(postgres);
        await using var disposableFactory = factory;
        using var httpClient = client;

        var seeded = await AdminTestHost.SeedManyAsync(factory, 30, "m1t7-cursor-walk");

        List<(DateTimeOffset CreatedAt, Guid Id)> observed = [];
        string? cursor = null;
        var pages = 0;

        while (true)
        {
            var path = "/v1/admin/users?limit=10"
                + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await httpClient.SendAsync(
                AdminTestHost.Request(HttpMethod.Get, path, bearer));
            page.StatusCode.Should().Be(HttpStatusCode.OK);

            var json = await AccountTestHost.ReadJsonAsync(page);
            var items = json.GetProperty("items");
            items.GetArrayLength().Should().BeLessThanOrEqualTo(10);

            foreach (var element in items.EnumerateArray())
            {
                observed.Add((
                    DateTimeOffset.Parse(element.GetProperty("created_at").GetString()!),
                    Guid.Parse(element.GetProperty("id").GetString()!)));
            }

            pages++;
            pages.Should().BeLessThan(50, "cursor pagination must terminate");

            if (json.TryGetProperty("next_cursor", out var next))
            {
                cursor = next.GetString();
                cursor.Should().NotBeNullOrWhiteSpace(
                    "an intermediate page must carry a next_cursor");
            }
            else
            {
                cursor = null;
            }

            if (cursor is null)
            {
                break;
            }
        }

        pages.Should().BeGreaterThan(1, "30+ users at limit=10 needs several pages");

        // Every seeded user is observed exactly once — no skips, no repeats.
        observed.Select(entry => entry.Id).Should().Contain(seeded);
        observed.Select(entry => entry.Id).Should().OnlyHaveUniqueItems();

        // Global (created_at DESC) ordering holds across page boundaries.
        for (var i = 1; i < observed.Count; i++)
        {
            observed[i - 1].CreatedAt.Should().BeOnOrAfter(
                observed[i].CreatedAt, "pages must not reorder the list");
        }
    }

    [Fact]
    public async Task List_FilterByEmail_ReturnsOnlyMatching()
    {
        var (factory, client, adminId, bearer) = await CreateAdminHostAsync(postgres);
        await using var disposableFactory = factory;
        using var httpClient = client;

        // Mixed-case seed, lower-case filter: citext equality must match.
        var targetId = await AdminTestHost.SeedUserAsync(
            factory, "M1t7.Filter.Case@Example.test");
        await AdminTestHost.SeedUserAsync(factory, UniqueEmail("filter-other"));

        var path = "/v1/admin/users?filter%5Bemail%5D="
            + Uri.EscapeDataString("m1t7.filter.case@example.test");
        var response = await httpClient.SendAsync(
            AdminTestHost.Request(HttpMethod.Get, path, bearer));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await AccountTestHost.ReadJsonAsync(response);
        var items = body.GetProperty("items");
        items.GetArrayLength().Should().Be(1, "citext equality matches case-insensitively");
        Guid.Parse(items[0].GetProperty("id").GetString()!).Should().Be(targetId);

        // admin_list_users is audited with the query parameters in metadata_json.
        var auditRows = await AccountTestHost.AuditRowsAsync(factory, adminId);
        auditRows.Should().Contain(row =>
            row.Action == AuditActions.AdminListUsers
            && row.ActorUserId == adminId
            && (row.MetadataJson ?? string.Empty).Contains("filter[email]"));
    }

    [Fact]
    public async Task List_FilterByRole_ReturnsOnlyAdmins()
    {
        var (factory, client, _, bearer) = await CreateAdminHostAsync(postgres);
        await using var disposableFactory = factory;
        using var httpClient = client;

        var adminOne = await AdminTestHost.SeedUserAsync(
            factory, UniqueEmail("filter-admin-1"), UserRole.Admin);
        var adminTwo = await AdminTestHost.SeedUserAsync(
            factory, UniqueEmail("filter-admin-2"), UserRole.Admin);
        var plain = await AdminTestHost.SeedUserAsync(factory, UniqueEmail("filter-plain"));

        var response = await httpClient.SendAsync(
            AdminTestHost.Request(
                HttpMethod.Get, "/v1/admin/users?filter%5Brole%5D=Admin", bearer));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await AccountTestHost.ReadJsonAsync(response);
        var items = body.GetProperty("items");

        var ids = items.EnumerateArray()
            .Select(element => Guid.Parse(element.GetProperty("id").GetString()!))
            .ToList();
        ids.Should().Contain([adminOne, adminTwo]);
        ids.Should().NotContain(plain);

        foreach (var element in items.EnumerateArray())
        {
            element.GetProperty("role").GetString().Should().Be("Admin");
        }
    }

    [Fact]
    public async Task List_LimitOutOfRange_Returns400()
    {
        var (factory, client, _, bearer) = await CreateAdminHostAsync(postgres);
        await using var disposableFactory = factory;
        using var httpClient = client;

        foreach (var limit in new[] { "101", "0" })
        {
            var response = await httpClient.SendAsync(
                AdminTestHost.Request(HttpMethod.Get, $"/v1/admin/users?limit={limit}", bearer));
            var problem = await AccountTestHost.ReadProblemAsync(
                response, HttpStatusCode.BadRequest);
            problem.GetProperty("type").GetString().Should().Be("/errors/validation");

            var errors = problem.GetProperty("errors").EnumerateArray()
                .Select(element => element.GetProperty("name").GetString());
            errors.Should().Contain("limit");
        }
    }

    [Fact]
    public async Task List_InvalidCursor_Returns400()
    {
        var (factory, client, _, bearer) = await CreateAdminHostAsync(postgres);
        await using var disposableFactory = factory;
        using var httpClient = client;

        // Valid base64url of "garbage" — strict-parse failure per §13.13.
        var response = await httpClient.SendAsync(
            AdminTestHost.Request(HttpMethod.Get, "/v1/admin/users?cursor=Z2FyYmFnZQ", bearer));

        var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.BadRequest);
        problem.GetProperty("type").GetString().Should().Be("/errors/validation");
        problem.GetProperty("errors").EnumerateArray()
            .Select(element => element.GetProperty("name").GetString())
            .Should().Contain("cursor");
    }

    [Fact]
    public async Task Get_ExistingUser_ReturnsSummary()
    {
        var (factory, client, adminId, bearer) = await CreateAdminHostAsync(postgres);
        await using var disposableFactory = factory;
        using var httpClient = client;

        var email = UniqueEmail("get-existing");
        var userId = await AdminTestHost.SeedUserAsync(
            factory, email, UserRole.User, UserStatus.Pending, mustChangePassword: true);

        var response = await httpClient.SendAsync(
            AdminTestHost.Request(HttpMethod.Get, $"/v1/admin/users/{userId}", bearer));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await AccountTestHost.ReadJsonAsync(response);

        body.GetProperty("id").GetString().Should().Be(userId.ToString());
        body.GetProperty("email").GetString().Should().Be(email);
        body.GetProperty("role").GetString().Should().Be("User");
        body.GetProperty("status").GetString().Should().Be("Pending");
        body.GetProperty("must_change_password").GetBoolean().Should().BeTrue();
        body.GetProperty("created_at").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("last_login_at").ValueKind.Should().Be(JsonValueKind.Null);

        // Same projection as the list — exactly seven members, no key material.
        body.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(new[]
        {
            "id", "email", "role", "status", "must_change_password",
            "created_at", "last_login_at",
        });

        // admin_read_user is audited against the target id.
        var auditRows = await AccountTestHost.AuditRowsAsync(factory, adminId);
        auditRows.Should().Contain(row =>
            row.Action == AuditActions.AdminReadUser
            && row.ActorUserId == adminId
            && row.TargetId == userId.ToString());
    }

    [Fact]
    public async Task Get_Missing_Returns404()
    {
        var (factory, client, _, bearer) = await CreateAdminHostAsync(postgres);
        await using var disposableFactory = factory;
        using var httpClient = client;

        var response = await httpClient.SendAsync(
            AdminTestHost.Request(HttpMethod.Get, $"/v1/admin/users/{Guid.NewGuid()}", bearer));

        var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.NotFound);
        problem.GetProperty("type").GetString().Should().Be("/errors/not-found");
    }

    [Fact]
    public async Task Create_HappyPath_ReturnsOtpInResponse_AndMarksUserPending()
    {
        var (factory, client, _, bearer) = await CreateAdminHostAsync(postgres);
        await using var disposableFactory = factory;
        using var httpClient = client;

        var email = UniqueEmail("create-happy");
        var response = await httpClient.SendAsync(
            AdminTestHost.Request(HttpMethod.Post, "/v1/admin/users", bearer, new { email }));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await AccountTestHost.ReadJsonAsync(response);
        var userId = Guid.Parse(body.GetProperty("id").GetString()!);

        response.Headers.Location!.ToString().Should().Be($"/v1/admin/users/{userId}");
        body.GetProperty("email").GetString().Should().Be(email);
        body.GetProperty("role").GetString().Should().Be("User", "role defaults to User");
        body.GetProperty("status").GetString().Should().Be("Pending");
        body.GetProperty("otp").GetString().Should().MatchRegex("^[A-Z2-7]{16}$");
        DateTimeOffset.TryParse(body.GetProperty("otp_expires_at").GetString(), out _)
            .Should().BeTrue();

        // DB row: Pending + forced change + NO key material (Q-E6).
        var user = await AccountTestHost.LoadUserAsync(factory, userId);
        user.Status.Should().Be(UserStatus.Pending);
        user.MustChangePassword.Should().BeTrue();
        user.Role.Should().Be(UserRole.User);
        user.PasswordHash.Should().NotBeNullOrWhiteSpace("a temp hash exists but is never shown");
        user.KdfSalt.Should().BeNull();
        user.KdfParams.Should().BeNull();
        user.DekWrapped.Should().BeNull();
        user.DekNonce.Should().BeNull();

        // OTP row: hashed at rest (48-byte salt||hash, not the 16-char raw),
        // expiring ≈ now + 7 days (Q-A2).
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var otpRow = await db.OneTimePasswords.AsNoTracking()
            .SingleAsync(row => row.UserId == userId);
        otpRow.OtpHash.Should().HaveCount(48, "PBKDF2 salt(16) || hash(32) — never the raw OTP");
        otpRow.ExpiresAt.Should().BeCloseTo(
            DateTimeOffset.UtcNow.AddDays(7), TimeSpan.FromMinutes(5));
        otpRow.UsedAt.Should().BeNull();
        otpRow.RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task Admin_Users_Post_CreatesUser_AndReturnsOtpOnce()
    {
        // Milestone exit criterion: the response carries exactly one OTP; the
        // DB row is Pending / must_change_password with a hashed otp_hash and
        // expires_at ≈ now + 7d; later calls never re-return that OTP and no
        // audit row ever contains it.
        var (factory, client, _, bearer) = await CreateAdminHostAsync(postgres);
        await using var disposableFactory = factory;
        using var httpClient = client;

        var emailOne = UniqueEmail("otp-once-1");
        var createOne = await httpClient.SendAsync(
            AdminTestHost.Request(
                HttpMethod.Post, "/v1/admin/users", bearer, new { email = emailOne }));
        createOne.StatusCode.Should().Be(HttpStatusCode.Created);

        var bodyOne = await AccountTestHost.ReadJsonAsync(createOne);
        var otpOne = bodyOne.GetProperty("otp").GetString()!;
        var userId = Guid.Parse(bodyOne.GetProperty("id").GetString()!);

        otpOne.Should().MatchRegex("^[A-Z2-7]{16}$");

        // Exactly one occurrence of the OTP in the raw response document.
        var rawOne = await createOne.Content.ReadAsStringAsync();
        ((rawOne.Length - rawOne.Replace(otpOne, string.Empty).Length) / otpOne.Length)
            .Should().Be(1, "the OTP string appears exactly once in the response body");

        // Exit row: status=Pending, must_change_password=true, hashed otp_hash,
        // expires_at ≈ now + 7d, and no key material columns written.
        var user = await AccountTestHost.LoadUserAsync(factory, userId);
        user.Status.Should().Be(UserStatus.Pending);
        user.MustChangePassword.Should().BeTrue();
        user.KdfSalt.Should().BeNull();
        user.KdfParams.Should().BeNull();
        user.DekWrapped.Should().BeNull();
        user.DekNonce.Should().BeNull();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var otpRow = await db.OneTimePasswords.AsNoTracking()
            .SingleAsync(row => row.UserId == userId);
        otpRow.OtpHash.Should().HaveCount(48);
        otpRow.ExpiresAt.Should().BeCloseTo(
            DateTimeOffset.UtcNow.AddDays(7), TimeSpan.FromMinutes(5));

        // Create flow audited: admin_create_user + admin_otp_issued (source=create).
        var rows = await AccountTestHost.AuditRowsAsync(factory, userId);
        rows.Where(row => row.TargetId == userId.ToString())
            .Select(row => row.Action)
            .Should().Contain([AuditActions.AdminCreateUser, AuditActions.AdminOtpIssued])
            .And.ContainSingle(action => action == AuditActions.AdminCreateUser);
        rows.Should().OnlyContain(row => row.Hmac != null && row.Hmac.Length == 32);

        // A second create returns a DIFFERENT OTP and never re-mentions the first.
        var emailTwo = UniqueEmail("otp-once-2");
        var createTwo = await httpClient.SendAsync(
            AdminTestHost.Request(
                HttpMethod.Post, "/v1/admin/users", bearer, new { email = emailTwo }));
        createTwo.StatusCode.Should().Be(HttpStatusCode.Created);
        var bodyTwo = await AccountTestHost.ReadJsonAsync(createTwo);
        var otpTwo = bodyTwo.GetProperty("otp").GetString()!;
        otpTwo.Should().NotBe(otpOne);
        var rawTwo = await createTwo.Content.ReadAsStringAsync();
        rawTwo.Should().NotContain(otpOne);

        // Reissue for the first user: again a fresh OTP, never otpOne.
        var reissue = await httpClient.SendAsync(
            AdminTestHost.Request(HttpMethod.Post, $"/v1/admin/users/{userId}/otp", bearer));
        reissue.StatusCode.Should().Be(HttpStatusCode.OK);
        var reissueBody = await AccountTestHost.ReadJsonAsync(reissue);
        var otpThree = reissueBody.GetProperty("otp").GetString()!;
        otpThree.Should().NotBe(otpOne).And.NotBe(otpTwo);
        var rawThree = await reissue.Content.ReadAsStringAsync();
        rawThree.Should().NotContain(otpOne);

        // The create-flow user now has exactly two admin_otp_issued rows.
        (await db.AuditLog.AsNoTracking().CountAsync(row =>
                row.Action == AuditActions.AdminOtpIssued
                && row.TargetId == userId.ToString()))
            .Should().Be(2, "create-flow issuance + reissue, one audit row each");

        // Q-A3: the raw OTP never lands in any audit metadata. The jsonb
        // column is projected bare — `?? ''` inside the EF projection would
        // make Npgsql hand Postgres an untyped '' literal the server refuses
        // to coerce to jsonb (22P02), so the coalesce runs client-side.
        var allMetadata = await db.AuditLog.AsNoTracking()
            .Select(row => row.MetadataJson)
            .ToListAsync();
        allMetadata
            .Where(metadata => (metadata ?? string.Empty).Contains(otpOne))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Create_DuplicateEmail_Returns409()
    {
        var (factory, client, _, bearer) = await CreateAdminHostAsync(postgres);
        await using var disposableFactory = factory;
        using var httpClient = client;

        var email = UniqueEmail("dup");
        await AdminTestHost.SeedUserAsync(factory, email);

        // Exact duplicate → 409 with the named §13.12 error class.
        var duplicate = await httpClient.SendAsync(
            AdminTestHost.Request(HttpMethod.Post, "/v1/admin/users", bearer, new { email }));
        var problem = await AccountTestHost.ReadProblemAsync(duplicate, HttpStatusCode.Conflict);
        problem.GetProperty("type").GetString().Should().Be("/errors/email-already-exists");

        // citext: a case-variant of an existing address is also a duplicate.
        var caseVariant = await httpClient.SendAsync(
            AdminTestHost.Request(HttpMethod.Post, "/v1/admin/users", bearer,
                new { email = email.ToUpperInvariant() }));
        (await AccountTestHost.ReadProblemAsync(caseVariant, HttpStatusCode.Conflict))
            .GetProperty("type").GetString().Should().Be("/errors/email-already-exists");
    }

    [Fact]
    public async Task Create_ByAdmin_AllowsAdminRole_TargetUserIsAdmin()
    {
        var (factory, client, _, bearer) = await CreateAdminHostAsync(postgres);
        await using var disposableFactory = factory;
        using var httpClient = client;

        var email = UniqueEmail("create-admin");
        var response = await httpClient.SendAsync(
            AdminTestHost.Request(
                HttpMethod.Post, "/v1/admin/users", bearer, new { email, role = "Admin" }));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await AccountTestHost.ReadJsonAsync(response);
        var userId = Guid.Parse(body.GetProperty("id").GetString()!);
        body.GetProperty("role").GetString().Should().Be("Admin");

        var user = await AccountTestHost.LoadUserAsync(factory, userId);
        user.Role.Should().Be(UserRole.Admin, "the users.role column carries the role");

        // …and the Identity role membership row exists (M1T4 role tables).
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var adminRoleId = (await db.IdentityRoles.AsNoTracking()
            .SingleAsync(role => role.NormalizedName == "ADMIN")).Id;
        (await db.Set<IdentityUserRole<Guid>>().AsNoTracking()
                .AnyAsync(link => link.UserId == userId && link.RoleId == adminRoleId))
            .Should().BeTrue("AddToRoleAsync links the new admin to the Admin role");
    }

    [Fact]
    public async Task Reissue_HappyPath_ReturnsNewOtp_AndRevokesPriorUnused()
    {
        var (factory, client, _, bearer) = await CreateAdminHostAsync(postgres);
        await using var disposableFactory = factory;
        using var httpClient = client;

        var created = await AdminTestHost.CreateUserAsync(
            httpClient, bearer, UniqueEmail("reissue"));
        var userId = Guid.Parse(created.GetProperty("id").GetString()!);
        var firstOtp = created.GetProperty("otp").GetString()!;

        var reissue = await httpClient.SendAsync(
            AdminTestHost.Request(HttpMethod.Post, $"/v1/admin/users/{userId}/otp", bearer));
        reissue.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await AccountTestHost.ReadJsonAsync(reissue);
        var secondOtp = body.GetProperty("otp").GetString()!;
        secondOtp.Should().MatchRegex("^[A-Z2-7]{16}$").And.NotBe(firstOtp);
        DateTimeOffset.TryParse(body.GetProperty("otp_expires_at").GetString(), out _)
            .Should().BeTrue();

        // Q-A3/SQ-6: the prior unused OTP is revoked, the new one is active.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var rows = await db.OneTimePasswords.AsNoTracking()
            .Where(row => row.UserId == userId)
            .OrderBy(row => row.CreatedAt)
            .ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].RevokedAt.Should().NotBeNull("regenerate revokes prior unused OTPs");
        rows[0].UsedAt.Should().BeNull();
        rows[1].RevokedAt.Should().BeNull();
        rows[1].OtpHash.Should().NotBeEquivalentTo(rows[0].OtpHash);

        // Service-level truth: only the reissued OTP verifies.
        var otpService = scope.ServiceProvider.GetRequiredService<IOtpService>();
        (await otpService.VerifyAsync(userId, secondOtp, CancellationToken.None))
            .Should().BeTrue();
        (await otpService.VerifyAsync(userId, firstOtp, CancellationToken.None))
            .Should().BeFalse("the revoked OTP must never verify again");
    }

    [Fact]
    public async Task Reissue_DisabledUser_Returns400()
    {
        var (factory, client, _, bearer) = await CreateAdminHostAsync(postgres);
        await using var disposableFactory = factory;
        using var httpClient = client;

        var userId = await AdminTestHost.SeedUserAsync(
            factory, UniqueEmail("reissue-disabled"), UserRole.User, UserStatus.Disabled);

        var response = await httpClient.SendAsync(
            AdminTestHost.Request(HttpMethod.Post, $"/v1/admin/users/{userId}/otp", bearer));

        var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.BadRequest);
        problem.GetProperty("type").GetString().Should().Be("/errors/validation");
    }

    [Fact]
    public async Task Create_And_Reissue_AreAudited()
    {
        var (factory, client, adminId, bearer) = await CreateAdminHostAsync(postgres);
        await using var disposableFactory = factory;
        using var httpClient = client;

        var created = await AdminTestHost.CreateUserAsync(
            httpClient, bearer, UniqueEmail("audit"));
        var userId = Guid.Parse(created.GetProperty("id").GetString()!);

        var reissue = await httpClient.SendAsync(
            AdminTestHost.Request(HttpMethod.Post, $"/v1/admin/users/{userId}/otp", bearer));
        reissue.StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = await AccountTestHost.AuditRowsAsync(factory, userId);
        var targeted = rows.Where(row => row.TargetId == userId.ToString()).ToList();

        targeted.Select(row => row.Action).Should().Equal(
            AuditActions.AdminCreateUser,
            AuditActions.AdminOtpIssued,
            AuditActions.AdminOtpIssued);

        targeted.Should().OnlyContain(row =>
            row.ActorUserId == adminId
            && row.ActorRole == "Admin"
            && row.Ip != null
            && row.Hmac != null
            && row.Hmac.Length == 32,
            "every admin-event row carries actor, ip and a full HMAC");

        // The HMAC chain survives the two new actions.
        using var scope = factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
        (await audit.VerifyChainAsync(CancellationToken.None)).Should().BeNull(
            "the HMAC chain stays intact after create + reissue");
    }
}
