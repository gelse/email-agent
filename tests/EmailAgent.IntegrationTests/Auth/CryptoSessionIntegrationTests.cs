using System.Net;
using System.Text.Json;
using EmailAgent.Api.Auth;
using EmailAgent.Core.Audit;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.IntegrationTests.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace EmailAgent.IntegrationTests.Auth;

// ---------------------------------------------------------------------------
// M5T1 (SQ-M5-1 / §13.17 / SQ-M5-6): the session-key envelope over the wire.
// GET /v1/account/kdf + POST /v1/account/unlock mint an opaque X-Crypto-Session
// token — the DEK is wrapped in the in-process store and never rides the wire
// again — every /v1/user/* route is judged on that token (403
// /errors/crypto-locked for a locked vault vs. the 401 of an absent JWT), the
// revocation cascades (logout, re-unlock, refresh-family reuse) re-lock the
// vault, a process restart invalidates everything (no persistence path), and
// the idle/absolute timeouts bound a live session. The unlock flow lives in
// the UNGUARDED account namespace, so no fact needs a session to obtain one.
// Tests never call a production service (AGENTS.md hard constraint 3).
// ---------------------------------------------------------------------------

[Collection("Postgres")]
public class CryptoSessionIntegrationTests(PostgresFixture postgres)
{
    private const string LegacyDekHeader = "X-User-Dek";

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static Task<HttpResponseMessage> SourcesGetAsync(
        HttpClient client, string bearer, string? session)
        => client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Get, "/v1/user/sources", bearer, session: session));

    /// <summary>
    /// Asserts the §13.12 document the middleware emits for a locked vault:
    /// 403 <c>/errors/crypto-locked</c>, the "Crypto session locked" title and
    /// a detail that tells the caller to unlock and retry.
    /// </summary>
    private static async Task<JsonElement> ReadCryptoLockedAsync(
        HttpResponseMessage response)
    {
        var problem = await AccountTestHost.ReadProblemAsync(
            response, HttpStatusCode.Forbidden);
        problem.GetProperty("type").GetString().Should()
            .Be(CryptoSessionMiddleware.CryptoLockedErrorType);
        problem.GetProperty("title").GetString().Should().Be("Crypto session locked");
        problem.GetProperty("detail").GetString().Should()
            .Contain("session", "the detail says how to recover the vault");
        return problem;
    }

    /// <summary>
    /// Seeds a login-capable user: real Identity password hash (so password
    /// login works), dummy DEK material (must_complete_first_login evaluates
    /// false) and the "User" Identity role LINK — login/refresh issue the JWT
    /// roles through <c>UserManager.GetRolesAsync</c> and every /v1/user/* route
    /// is <c>RequireRole("User")</c>, which runs BEFORE the crypto-session gate.
    /// </summary>
    private static async Task<Guid> SeedLoginUserAsync(
        WebApplicationFactory<Program> factory, string email)
    {
        await AdminTestHost.EnsureRolesAsync(factory);
        var userId = await AccountTestHost.SeedUserAsync(
            factory, email, mustChangePassword: false, withDek: true);

        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await userManager.FindByIdAsync(userId.ToString());
        user.Should().NotBeNull("seeding must have persisted the account");
        (await userManager.AddToRoleAsync(user!, "User")).Succeeded.Should().BeTrue(
            "the login/refresh JWT must carry the User role claim");
        return userId;
    }

    /// <summary>Logs in (asserts 200) and returns bearer + raw ea_refresh cookie.</summary>
    private static async Task<(string Bearer, string Cookie)> LoginAsync(
        HttpClient client, string email)
    {
        using var response = await AccountTestHost.PostLoginAsync(
            client, email, AccountTestHost.TempPassword);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "login succeeds for a seeded user");
        var body = await AccountTestHost.ReadJsonAsync(response);
        var bearer = body.GetProperty("access_token").GetString();
        bearer.Should().NotBeNullOrWhiteSpace();
        var cookie = AccountTestHost.RefreshCookieFrom(response);
        return (bearer!, cookie);
    }

    /// <summary>
    /// Raw information_schema read (no ORM, no production service) for the
    /// "no crypto table exists" proof — with a control pattern so an empty
    /// result can never come from a broken query.
    /// </summary>
    private static async Task<List<string>> QueryTablesAsync(
        string connectionString, string namePattern)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT table_name FROM information_schema.tables "
            + "WHERE table_schema NOT IN ('pg_catalog', 'information_schema') "
            + "AND table_name ILIKE @pattern";
        command.Parameters.AddWithValue("pattern", namePattern);

        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    /// <summary>The member names of one audit metadata_json document.</summary>
    private static IReadOnlyCollection<string> MetadataKeys(string? metadataJson)
    {
        using var document = JsonDocument.Parse(metadataJson ?? "{}");
        return document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ToList();
    }

    // -----------------------------------------------------------------------
    // 1-2. Unlock happy path + the data route it unlocks
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Unlock_HappyPath_WrapsDekAndReturnsToken()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        var userId = await AdminTestHost.SeedUserAsync(
            factory, SourcesTestHost.UniqueEmail("m5t1-unlock"));
        await SourcesTestHost.SetKdfMaterialAsync(factory, userId);
        var bearer = await AdminTestHost.BearerAsync(factory, userId, "User");
        var dek = SourcesTestHost.NewDek();

        // Pre-step, BEFORE any session exists: the browser needs this material
        // precisely while the vault is locked, so GET /v1/account/kdf is JWT-only
        // and never cacheable.
        using var kdf = await client.SendAsync(
            SourcesTestHost.Request(HttpMethod.Get, "/v1/account/kdf", bearer));
        kdf.StatusCode.Should().Be(HttpStatusCode.OK);
        kdf.Headers.CacheControl.Should().NotBeNull("KDF material must carry Cache-Control");
        kdf.Headers.CacheControl!.NoStore.Should().BeTrue(
            "KDF material must be no-store (same rule as the M2 route)");
        var kdfBody = await AccountTestHost.ReadJsonAsync(kdf);
        Convert.FromBase64String(kdfBody.GetProperty("kdf_salt").GetString()!)
            .Length.Should().Be(16, "the Argon2id salt is the users.kdf_salt row");
        Convert.FromBase64String(kdfBody.GetProperty("dek_wrapped").GetString()!)
            .Length.Should().Be(48, "ciphertext + 16-byte GCM tag");
        Convert.FromBase64String(kdfBody.GetProperty("dek_nonce").GetString()!)
            .Length.Should().Be(12, "AES-GCM nonce length");
        var kdfParams = kdfBody.GetProperty("kdf_params");
        kdfParams.GetProperty("memory_kb").GetInt32().Should().BePositive();
        kdfParams.GetProperty("iterations").GetInt32().Should().BePositive();
        kdfParams.GetProperty("parallelism").GetInt32().Should().BePositive();
        kdfParams.GetProperty("hash_length").GetInt32().Should().BePositive();

        // Unlock: the server wraps the DEK under a fresh random session key and
        // returns the opaque token exactly once (SQ-M5-1).
        using var unlock = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Post, "/v1/account/unlock", bearer, new { dek }));
        unlock.StatusCode.Should().Be(HttpStatusCode.OK,
            "unlock must mint the session-key envelope: "
            + await unlock.Content.ReadAsStringAsync());
        unlock.Headers.CacheControl.Should().NotBeNull("the token response must carry Cache-Control");
        unlock.Headers.CacheControl!.NoStore.Should().BeTrue(
            "the raw token is shown once — never cacheable");
        var unlockBody = await AccountTestHost.ReadJsonAsync(unlock);
        unlockBody.GetProperty("crypto_session_id").GetGuid().Should().NotBeEmpty();
        var token = unlockBody.GetProperty("crypto_session_token").GetString();
        token.Should().NotBeNullOrWhiteSpace();
        token.Should().NotBe(dek,
            "the DEK is wrapped INSIDE the entry — it is never itself the credential");

        // The token is the credential for user data…
        using var withToken = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Get, "/v1/user/sources", bearer, session: token));
        withToken.StatusCode.Should().Be(HttpStatusCode.OK,
            "the minted token unlocks /v1/user/*");

        // …and the DEK presented as that credential is not: only the store's
        // token hash authenticates a session.
        using var withDekAsSession = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Get, "/v1/user/sources", bearer, session: dek));
        await ReadCryptoLockedAsync(withDekAsSession);
    }

    [Fact]
    public async Task UserData_WithCryptoSession_Succeeds()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        var userId = await AdminTestHost.SeedUserAsync(
            factory, SourcesTestHost.UniqueEmail("m5t1-data"));
        var bearer = await AdminTestHost.BearerAsync(factory, userId, "User");
        var session = await SourcesTestHost.UnlockAsync(
            client, bearer, SourcesTestHost.NewDek());

        using var sources = await SourcesGetAsync(client, bearer, session);
        sources.StatusCode.Should().Be(HttpStatusCode.OK,
            "a valid X-Crypto-Session passes the gate for the sources list");
        var sourcesPage = await AccountTestHost.ReadJsonAsync(sources);
        sourcesPage.GetProperty("items").ValueKind.Should().Be(JsonValueKind.Array);

        using var actions = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Get, "/v1/user/actions", bearer, session: session));
        actions.StatusCode.Should().Be(HttpStatusCode.OK,
            "the SAME gate covers the actions list — the guard is generic "
            + "across /v1/user/*, not sources-specific");
        var actionsPage = await AccountTestHost.ReadJsonAsync(actions);
        actionsPage.GetProperty("items").ValueKind.Should().Be(JsonValueKind.Array);
    }

    // -----------------------------------------------------------------------
    // 3. Locked vault → the typed 403
    // -----------------------------------------------------------------------

    [Fact]
    public async Task LockedSession_Returns403_CryptoLocked()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        var userId = await AdminTestHost.SeedUserAsync(
            factory, SourcesTestHost.UniqueEmail("m5t1-locked"));
        var bearer = await AdminTestHost.BearerAsync(factory, userId, "User");

        // Locked = no session presented at all (the SPA has not unlocked yet).
        using var absent = await SourcesGetAsync(client, bearer, session: null);
        await ReadCryptoLockedAsync(absent);

        // Locked = a token that does not authenticate any live session
        // (forged, stale, or revoked — same typed outcome, Absent vs. Locked).
        using var forged = await SourcesGetAsync(client, bearer, session: "forged-session-token");
        await ReadCryptoLockedAsync(forged);

        // Unlocking flips exactly the request that carries the token.
        var session = await SourcesTestHost.UnlockAsync(
            client, bearer, SourcesTestHost.NewDek());
        using var unlocked = await SourcesGetAsync(client, bearer, session);
        unlocked.StatusCode.Should().Be(HttpStatusCode.OK,
            "unlocking is what changes the outcome — the gate is per-request");

        // Still locked for a caller that presents no token, even though a live
        // session exists: the header is the credential, not the account state.
        using var stillAbsent = await SourcesGetAsync(client, bearer, session: null);
        await ReadCryptoLockedAsync(stillAbsent);
    }

    // -----------------------------------------------------------------------
    // 4-5. Revocation cascades: logout and re-unlock
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Logout_RevokesCryptoSession()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        var email = SourcesTestHost.UniqueEmail("m5t1-logout");
        var userId = await SeedLoginUserAsync(factory, email);
        var (bearer, cookie) = await LoginAsync(client, email);
        var session = await SourcesTestHost.UnlockAsync(
            client, bearer, SourcesTestHost.NewDek());

        using var beforeLogout = await SourcesGetAsync(client, bearer, session);
        beforeLogout.StatusCode.Should().Be(HttpStatusCode.OK);

        // Logout carries only the ea_refresh cookie (§13.11) — yet it must
        // re-lock the vault along with the refresh token.
        using var logout = await client.SendAsync(AccountTestHost.LogoutRequest(cookie));
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);
        AccountTestHost.AssertCookieCleared(logout);

        // The JWT is still alive (900s TTL), so the outcome MUST be the 403 —
        // not a 401: the crypto session died, the access token did not.
        using var afterLogout = await SourcesGetAsync(client, bearer, session);
        await ReadCryptoLockedAsync(afterLogout);

        var rows = await AccountTestHost.AuditRowsAsync(factory, userId);
        rows.Should().ContainSingle(a => a.Action == AuditActions.CryptoSessionRevoked)
            .Which.MetadataJson.Should().Contain("\"reason\":\"logout\"",
                "the cascade audit row names why the session died (SQ-M5-1)");
    }

    [Fact]
    public async Task ReUnlock_ReplacesExistingSession()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        var userId = await AdminTestHost.SeedUserAsync(
            factory, SourcesTestHost.UniqueEmail("m5t1-reunlock"));
        var bearer = await AdminTestHost.BearerAsync(factory, userId, "User");

        var firstToken = await SourcesTestHost.UnlockAsync(
            client, bearer, SourcesTestHost.NewDek());
        using var firstLive = await SourcesGetAsync(client, bearer, firstToken);
        firstLive.StatusCode.Should().Be(HttpStatusCode.OK);

        // Idempotent replace (SQ-M5-1): a second unlock revokes the first
        // session and mints a NEW token — never the old one.
        var secondToken = await SourcesTestHost.UnlockAsync(
            client, bearer, SourcesTestHost.NewDek());
        secondToken.Should().NotBe(firstToken,
            "the token is emitted exactly once per unlock");

        using var secondLive = await SourcesGetAsync(client, bearer, secondToken);
        secondLive.StatusCode.Should().Be(HttpStatusCode.OK,
            "the fresh session is the live one");

        // The replaced session is revoked — a stale tab must re-unlock.
        using var stale = await SourcesGetAsync(client, bearer, firstToken);
        await ReadCryptoLockedAsync(stale);

        var rows = await AccountTestHost.AuditRowsAsync(factory, userId);
        rows.Count(a => a.Action == AuditActions.CryptoSessionOpened).Should().Be(2,
            "one crypto_session_opened row per unlock");
        rows.Should().ContainSingle(a => a.Action == AuditActions.CryptoSessionRevoked)
            .Which.MetadataJson.Should().Contain("\"reason\":\"replaced\"");
    }

    /// <summary>401 vs. 403: no/invalid JWT is refused before the gate ever
    /// runs; a live JWT over a locked vault is the typed 403 (SQ-M5-1).</summary>
    [Fact]
    public async Task AbsentJwt_Returns401_WhileLockedSession_Returns403()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        var userId = await AdminTestHost.SeedUserAsync(
            factory, SourcesTestHost.UniqueEmail("m5t1-401-403"));
        var bearer = await AdminTestHost.BearerAsync(factory, userId, "User");

        // No credentials at all → the bearer challenge, 401.
        using var anonymous = await client.GetAsync("/v1/user/sources");
        var anonymousProblem = await AccountTestHost.ReadProblemAsync(
            anonymous, HttpStatusCode.Unauthorized);
        anonymousProblem.GetProperty("type").GetString().Should().Be("/errors/unauthorized");

        // Unparseable/garbage JWT → same 401 (authentication never succeeds).
        using var garbage = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Get, "/v1/user/sources", bearer: "not-a-real-jwt"));
        var garbageProblem = await AccountTestHost.ReadProblemAsync(
            garbage, HttpStatusCode.Unauthorized);
        garbageProblem.GetProperty("type").GetString().Should().Be("/errors/unauthorized");

        // Valid JWT + locked vault → the OTHER status: 403 crypto-locked.
        using var locked = await SourcesGetAsync(client, bearer, session: null);
        await ReadCryptoLockedAsync(locked);

        // Control: the very same JWT works once the vault is unlocked, which
        // proves the 401/403 split above tracked credentials vs. session state.
        var session = await SourcesTestHost.UnlockAsync(
            client, bearer, SourcesTestHost.NewDek());
        using var unlocked = await SourcesGetAsync(client, bearer, session);
        unlocked.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // -----------------------------------------------------------------------
    // 6-7. The legacy M2 DEK header is inert
    // -----------------------------------------------------------------------

    [Fact]
    public async Task XUserDekHeader_Ignored_OnUserDataRoute()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        var userId = await AdminTestHost.SeedUserAsync(
            factory, SourcesTestHost.UniqueEmail("m5t1-legacy-header"));
        var bearer = await AdminTestHost.BearerAsync(factory, userId, "User");
        var dek = SourcesTestHost.NewDek();
        var session = await SourcesTestHost.UnlockAsync(client, bearer, dek);

        // The M2 header riding ALONGSIDE a valid session must be inert: the
        // request succeeds because of the session, not because of the DEK —
        // the server never reads X-User-Dek any more (DekHeaderHandler is gone).
        using var withBoth = SourcesTestHost.Request(
            HttpMethod.Get, "/v1/user/sources", bearer, session: session);
        withBoth.Headers.Add(LegacyDekHeader, dek);
        using var withBothResponse = await client.SendAsync(withBoth);
        withBothResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "the legacy X-User-Dek header must not disturb the session-gated route");

        // And on its own it grants nothing: only X-Crypto-Session unlocks.
        using var legacyOnly = SourcesTestHost.Request(
            HttpMethod.Get, "/v1/user/sources", bearer);
        legacyOnly.Headers.Add(LegacyDekHeader, dek);
        using var legacyOnlyResponse = await client.SendAsync(legacyOnly);
        await ReadCryptoLockedAsync(legacyOnlyResponse);
    }

    // -----------------------------------------------------------------------
    // 8-9. Timeout semantics (SQ-M5-6): sliding idle, hard absolute cap
    // -----------------------------------------------------------------------

    [Fact]
    public async Task IdleTimeout_Sliding_RefreshedByUserDataRequest()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString)
            .WithWebHostBuilder(builder => builder.UseSetting(
                "Session:IdleTimeout", "00:00:06"));
        using var client = AccountTestHost.CreateClient(factory);

        var userId = await AdminTestHost.SeedUserAsync(
            factory, SourcesTestHost.UniqueEmail("m5t1-idle"));
        var bearer = await AdminTestHost.BearerAsync(factory, userId, "User");
        var session = await SourcesTestHost.UnlockAsync(
            client, bearer, SourcesTestHost.NewDek());

        // t0: idle deadline t0+6s.
        // t~4: first request succeeds AND slides the deadline to t4+6 = t10.
        await Task.Delay(TimeSpan.FromSeconds(4));
        using var first = await SourcesGetAsync(client, bearer, session);
        first.StatusCode.Should().Be(HttpStatusCode.OK,
            "the session must still be inside its first 6s idle window");

        // t~8: PAST the original t0+6 deadline — only a sliding refresh keeps
        // this alive (the t4 touch moved the deadline to t10). Without sliding
        // this request would already be 403.
        await Task.Delay(TimeSpan.FromSeconds(4));
        using var second = await SourcesGetAsync(client, bearer, session);
        second.StatusCode.Should().Be(HttpStatusCode.OK,
            "idle is SLIDING: activity at t~4 refreshed a deadline that would "
            + "otherwise have expired at t0+6s (this request runs past it)");

        // t~17: last touch was t~8 → deadline t14; 3s past it → expired.
        // Margins: 2s before t10 for the second request, ~3s past t14 here.
        await Task.Delay(TimeSpan.FromSeconds(9));
        using var third = await SourcesGetAsync(client, bearer, session);
        await ReadCryptoLockedAsync(third);
    }

    [Fact]
    public async Task AbsoluteTimeout_NotExtendedByJwtRefresh()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString)
            .WithWebHostBuilder(builder => builder.UseSetting(
                "Session:AbsoluteTimeout", "00:00:10"));
        using var client = AccountTestHost.CreateClient(factory);

        var email = SourcesTestHost.UniqueEmail("m5t1-absolute");
        await SeedLoginUserAsync(factory, email);
        var (bearer1, cookie) = await LoginAsync(client, email);
        var session = await SourcesTestHost.UnlockAsync(
            client, bearer1, SourcesTestHost.NewDek());

        // t0: absolute deadline t0+10s (the 30m default idle is clamped to it).
        // t~4: refresh rotates the JWT — it does NOT touch the crypto store.
        await Task.Delay(TimeSpan.FromSeconds(4));
        using var refresh = await client.SendAsync(AccountTestHost.RefreshRequest(cookie));
        refresh.StatusCode.Should().Be(HttpStatusCode.OK);
        var refreshBody = await AccountTestHost.ReadJsonAsync(refresh);
        var bearer2 = refreshBody.GetProperty("access_token").GetString();
        bearer2.Should().NotBeNullOrWhiteSpace().And.NotBe(bearer1,
            "refresh issues a fresh access token");

        // The refreshed JWT is accepted and the session alive; the touch would
        // slide idle by 30m if the absolute cap did not clamp it.
        using var alive = await SourcesGetAsync(client, bearer2!, session);
        alive.StatusCode.Should().Be(HttpStatusCode.OK);

        // t~12: past the absolute deadline (t0+10). The JWT is still valid
        // (900s TTL — hence 403, not 401), and idle alone would still be alive
        // (t~4 + 30m): only the absolute cap can explain this refusal.
        await Task.Delay(TimeSpan.FromSeconds(8));
        using var expired = await SourcesGetAsync(client, bearer2!, session);
        await ReadCryptoLockedAsync(expired);
    }

    // -----------------------------------------------------------------------
    // 10-11. Reuse cascade and process restart
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RefreshFamilyRevocation_RevokesCryptoSession()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        var email = SourcesTestHost.UniqueEmail("m5t1-family");
        var userId = await SeedLoginUserAsync(factory, email);
        var (bearer, cookie1) = await LoginAsync(client, email);
        var session = await SourcesTestHost.UnlockAsync(
            client, bearer, SourcesTestHost.NewDek());

        using var beforeReuse = await SourcesGetAsync(client, bearer, session);
        beforeReuse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Rotate once, then replay the rotated-out cookie: reuse detection
        // burns the whole family (M1T9)…
        using var rotate = await client.SendAsync(AccountTestHost.RefreshRequest(cookie1));
        rotate.StatusCode.Should().Be(HttpStatusCode.OK);
        using var reuse = await client.SendAsync(AccountTestHost.RefreshRequest(cookie1));
        await AccountTestHost.ReadProblemAsync(reuse, HttpStatusCode.Unauthorized);

        // …and M5T1 makes that cascade re-lock the vault (SQ-M5-1): the JWT
        // from the original login is still valid, so the outcome is 403.
        using var afterReuse = await SourcesGetAsync(client, bearer, session);
        await ReadCryptoLockedAsync(afterReuse);

        var rows = await AccountTestHost.AuditRowsAsync(factory, userId);
        rows.Should().ContainSingle(a => a.Action == AuditActions.CryptoSessionRevoked)
            .Which.MetadataJson.Should().Contain("\"reason\":\"refresh_family_revoked\"",
                "reuse detection's cascade is audited with its own reason");
    }

    [Fact]
    public async Task Restart_InvalidatesAllSessions()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        string bearer;
        string session;

        // Host A: mint a session in this process's store.
        await using (var factoryA = AccountTestHost.CreateFactory(connectionString))
        {
            using var clientA = AccountTestHost.CreateClient(factoryA);
            var userId = await AdminTestHost.SeedUserAsync(
                factoryA, SourcesTestHost.UniqueEmail("m5t1-restart"));
            bearer = await AdminTestHost.BearerAsync(factoryA, userId, "User");
            session = await SourcesTestHost.UnlockAsync(
                clientA, bearer, SourcesTestHost.NewDek());

            using var aliveA = await SourcesGetAsync(clientA, bearer, session);
            aliveA.StatusCode.Should().Be(HttpStatusCode.OK);
        } // factory A — and its in-process store — is gone here

        // Host B: SAME database and signing key, fresh process store.
        await using (var factoryB = AccountTestHost.CreateFactory(connectionString))
        {
            using var clientB = AccountTestHost.CreateClient(factoryB);

            // The JWT still authenticates (same key, same account) — only the
            // session died with the process, so this is 403 crypto-locked.
            using var cold = await SourcesGetAsync(clientB, bearer, session);
            await ReadCryptoLockedAsync(cold);

            // Re-unlock succeeds: the account survived, the session did not —
            // proving there is no persistence path for sessions (no table).
            var revived = await SourcesTestHost.UnlockAsync(
                clientB, bearer, SourcesTestHost.NewDek());
            using var warm = await SourcesGetAsync(clientB, bearer, revived);
            warm.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    // -----------------------------------------------------------------------
    // 12. The gate is generic across user-data routes
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UserDataRoutes_EnforceCryptoSession_OnSourcesAndActions()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        var userId = await AdminTestHost.SeedUserAsync(
            factory, SourcesTestHost.UniqueEmail("m5t1-routes"));
        var bearer = await AdminTestHost.BearerAsync(factory, userId, "User");

        // Sources list: refused while locked, served once unlocked.
        using var sourcesLocked = await SourcesGetAsync(client, bearer, session: null);
        await ReadCryptoLockedAsync(sourcesLocked);

        var session = await SourcesTestHost.UnlockAsync(
            client, bearer, SourcesTestHost.NewDek());

        using var sourcesOpen = await SourcesGetAsync(client, bearer, session);
        sourcesOpen.StatusCode.Should().Be(HttpStatusCode.OK);

        // Actions list: the SAME 403 — the middleware guards the namespace,
        // it is not a sources-only check.
        using var actionsLocked = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Get, "/v1/user/actions", bearer));
        await ReadCryptoLockedAsync(actionsLocked);

        using var actionsOpen = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Get, "/v1/user/actions", bearer, session: session));
        actionsOpen.StatusCode.Should().Be(HttpStatusCode.OK);

        // Actions item route: the gate runs BEFORE the handler (403 while
        // locked), and the handler's own typed 404 survives unlocking.
        var missingId = Guid.NewGuid();
        using var itemLocked = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Get, $"/v1/user/actions/{missingId}", bearer));
        await ReadCryptoLockedAsync(itemLocked);

        using var itemOpen = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Get, $"/v1/user/actions/{missingId}", bearer, session: session));
        var notFound = await AccountTestHost.ReadProblemAsync(
            itemOpen, HttpStatusCode.NotFound);
        notFound.GetProperty("type").GetString().Should().Be("/errors/action-not-found");
    }

    // -----------------------------------------------------------------------
    // 13-14. Audit hygiene and the no-persistence-path property (SQ-M5-8)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Unlock_AuditRows_NoDekOrTokenInMetadata()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        var userId = await AdminTestHost.SeedUserAsync(
            factory, SourcesTestHost.UniqueEmail("m5t1-audit"));
        var bearer = await AdminTestHost.BearerAsync(factory, userId, "User");

        var dek1 = SourcesTestHost.NewDek();
        var dek2 = SourcesTestHost.NewDek();
        var token1 = await SourcesTestHost.UnlockAsync(client, bearer, dek1);
        var token2 = await SourcesTestHost.UnlockAsync(client, bearer, dek2);
        token1.Should().NotBe(token2);

        var rows = await AccountTestHost.AuditRowsAsync(factory, userId);

        // Not one audit row anywhere may carry a secret: neither raw token
        // (shown once at unlock) nor either DEK (never server-readable in
        // production — here the test's own inputs) may be stored as metadata.
        // Audit metadata is evidence, never a credential store (SQ-M5-8).
        var allMetadata = string.Join(
            "\n", rows.Select(r => r.MetadataJson).Where(m => m is not null));
        allMetadata.Should().NotContain(token1,
                "no audit row may carry a session token or a DEK")
            .And.NotContain(token2)
            .And.NotContain(dek1)
            .And.NotContain(dek2);

        var opened = rows.Where(r => r.Action == AuditActions.CryptoSessionOpened)
            .ToList();
        opened.Should().HaveCount(2, "one crypto_session_opened row per unlock");
        foreach (var row in opened)
        {
            MetadataKeys(row.MetadataJson).Should().Equal(
                new[] { "session_id" },
                "the success row proves a session exists — nothing else");
        }

        var revoked = rows.Where(r => r.Action == AuditActions.CryptoSessionRevoked)
            .ToList();
        revoked.Should().ContainSingle("only the second unlock replaced a live session");
        MetadataKeys(revoked[0].MetadataJson).Should()
            .BeEquivalentTo(new[] { "session_id", "reason" });
        revoked[0].MetadataJson.Should().Contain("\"reason\":\"replaced\"");
    }

    [Fact]
    public async Task Unlock_NoDbWritesForDek_NoCryptoTable()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        var userId = await AdminTestHost.SeedUserAsync(
            factory, SourcesTestHost.UniqueEmail("m5t1-nodb"));
        await SourcesTestHost.SetKdfMaterialAsync(factory, userId);
        var bearer = await AdminTestHost.BearerAsync(factory, userId, "User");

        // No crypto/session table exists — with a control pattern proving the
        // query itself works against this migrated database.
        (await QueryTablesAsync(connectionString, "%refresh%"))
            .Should().Contain("refresh_tokens", "control: the query sees real tables");
        (await QueryTablesAsync(connectionString, "%crypto%"))
            .Should().BeEmpty("sessions are RAM-only — no crypto table may exist");
        (await QueryTablesAsync(connectionString, "%session%"))
            .Should().BeEmpty("…nor any table named after the session store");

        var before = await AccountTestHost.LoadUserAsync(factory, userId);

        // Unlocking persists NOTHING key-related: the users row is byte-equal
        // before and after (the store holds the wrapped DEK in process RAM).
        using var unlock = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Post, "/v1/account/unlock", bearer, new { dek = SourcesTestHost.NewDek() }));
        unlock.StatusCode.Should().Be(HttpStatusCode.OK,
            "unlock must still mint a session: "
            + await unlock.Content.ReadAsStringAsync());

        var after = await AccountTestHost.LoadUserAsync(factory, userId);
        after.DekWrapped.Should().Equal(before.DekWrapped,
            "unlock must never rewrite users.dek_wrapped");
        after.DekNonce.Should().Equal(before.DekNonce,
            "unlock must never rewrite users.dek_nonce");
        after.KdfSalt.Should().Equal(before.KdfSalt,
            "unlock must never rewrite users.kdf_salt");
        after.KdfParams!.RootElement.GetRawText().Should()
            .Be(before.KdfParams!.RootElement.GetRawText(),
                "unlock must never rewrite users.kdf_params");
    }
}
