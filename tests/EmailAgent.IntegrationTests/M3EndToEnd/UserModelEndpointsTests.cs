using System.Net;
using System.Text.Json;
using EmailAgent.IntegrationTests.Endpoints;
using EmailAgent.Infrastructure.Enums;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace EmailAgent.IntegrationTests.M3EndToEnd;

// ---------------------------------------------------------------------------
// M3T7 step 4 (SQ-M3-6 / SQ-M3-12): GET /v1/user/models against Testcontainers
// Postgres + the real host. The three seeded rows are the task's own matrix —
// pass+enabled, fail+enabled, pass+disabled — and the assertion is that only
// the FIRST survives the server-side allow-list (enabled = true AND
// tool_calling_probe_result = 'Pass'), projected down to the fixed FOUR-member
// reduced DTO. No socket, no real provider (AGENTS.md test constraints): this
// suite never calls a probe at all, it seeds the rows directly.
//
// Isolation: one `m3t7-<guid>` marker per fact, blobs-then-rows cleanup in
// DisposeAsync, this collection's own Postgres container (M3E2E is
// non-parallel), audit rows never touched.
// ---------------------------------------------------------------------------

[Trait("Category", "M3EndToEnd")]
[Collection(M3E2ECollection.Name)]
public sealed class UserModelEndpointsTests(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>Seeded base_url — never contacted, and deliberately not a real host.</summary>
    private const string SeededBaseUrl = "http://m3t7-openai-mock.invalid/v1";

    private static readonly string[] DtoMembers =
    [
        "id",
        "display_name",
        "provider",
        "model_name",
    ];

    private readonly List<string> _markers = [];

    // -----------------------------------------------------------------------
    // The task's own fact (Acceptance criterion: reduced DTO, no admin fields)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Task step 4: three seeded rows (pass+enabled, fail+enabled,
    /// pass+disabled) and the endpoint answers with exactly the pass+enabled
    /// one — in the reduced projection, with no <c>base_url</c>, no
    /// <c>api_key</c>, no <c>created_by</c> and no price member anywhere.
    /// </summary>
    [Fact]
    public async Task UserModels_Endpoint_ReturnsReducedDto_NoApiKeyOrBaseUrl()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        var passEnabled = await M3TestHarness.SeedRowAsync(
            factory, host.AdminId, $"{host.Marker} pass enabled", $"{host.Marker}-a");
        await M3TestHarness.SeedRowAsync(
            factory,
            host.AdminId,
            $"{host.Marker} fail enabled",
            $"{host.Marker}-b",
            probeResult: ToolCallingProbeResult.Fail);
        await M3TestHarness.SeedRowAsync(
            factory,
            host.AdminId,
            $"{host.Marker} pass disabled",
            $"{host.Marker}-c",
            enabled: false);

        using var response = await M3TestHarness.SendAsync(
            host, HttpMethod.Get, "/v1/user/models?limit=100");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "an Admin caller is allowed (User, Admin)");
        M3TestHarness.AssertRateLimitHeaders(response, "GET 200");
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain(M3TestHarness.ApiKey, "no credential may reach a user response");
        raw.Should().NotContain("api_key", "not even as a member name");
        raw.Should().NotContain(SeededBaseUrl, "base_url would leak internal topology");
        raw.Should().NotContain("base_url");
        raw.Should().NotContain("created_by");
        raw.Should().NotContain("price_per_1k");

        using var document = JsonDocument.Parse(raw);
        var items = document.RootElement.GetProperty("items").EnumerateArray().ToList();
        items.Should().ContainSingle(
            "only enabled AND Pass rows pass the §11 allow-list — the other two are filtered out");
        var item = items[0];
        item.GetProperty("id").GetGuid().Should().Be(passEnabled);
        item.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo(
                DtoMembers, "the fixed FOUR-member reduced projection — nothing admin-only");
        item.GetProperty("display_name").GetString().Should().Be($"{host.Marker} pass enabled");
        item.GetProperty("provider").GetString().Should().Be(M3TestHarness.Provider);
        item.GetProperty("model_name").GetString().Should().Be($"{host.Marker}-a");

        // Typed-record proof: the published context binds the body exactly, so
        // a synthesized admin member would break the property count.
        var typed = JsonSerializer.Deserialize(
            raw, EmailAgent.Api.Endpoints.UserModelsJsonContext.Default.UserModelListResponse);
        typed.Should().NotBeNull();
        typed!.Items.Should().ContainSingle();
        typed.Items[0].Id.Should().Be(passEnabled);
        typed.Items[0].DisplayName.Should().Be($"{host.Marker} pass enabled");
        typeof(EmailAgent.Api.Endpoints.UserModelDto)
            .GetProperties().Select(property => property.Name)
            .Should().BeEquivalentTo(["Id", "DisplayName", "Provider", "ModelName"],
                "the server-side DTO itself has exactly four members");
    }

    // -----------------------------------------------------------------------
    // Roles (task step 4: the non-authenticated caller is rejected)
    // -----------------------------------------------------------------------

    /// <summary>
    /// <c>[Authorize(Roles = "User, Admin")]</c> in action: anonymous is a
    /// 401 RFC 7807 <c>/errors/unauthorized</c>, while BOTH an ordinary User
    /// and an Admin read the same list (exit criterion 1's "any authenticated
    /// user may pick").
    /// </summary>
    [Fact]
    public async Task UserModels_Roles_AnonymousRejected_UserAndAdminAllowed()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        var modelId = await M3TestHarness.SeedRowAsync(
            factory, host.AdminId, $"{host.Marker} pickable", $"{host.Marker}-a");

        using var anonymous = await host.Client.GetAsync("/v1/user/models");
        (await AccountTestHost.ReadProblemAsync(anonymous, HttpStatusCode.Unauthorized))
            .GetProperty("type").GetString().Should().Be("/errors/unauthorized");

        var userId = await AdminTestHost.SeedUserAsync(
            factory, $"{host.Marker}-user@example.test", UserRole.User);
        var userBearer = await AdminTestHost.BearerAsync(factory, userId, "User");

        // M5T1: the ordinary User unlocks too — each caller presents its OWN
        // X-Crypto-Session envelope (the session is judged per caller).
        var userSession = await SourcesTestHost.UnlockAsync(
            host.Client, userBearer, SourcesTestHost.NewDek());
        using var userResponse = await host.Client.SendAsync(AdminTestHost.Request(
            HttpMethod.Get, "/v1/user/models?limit=100", userBearer, session: userSession));
        userResponse.StatusCode.Should().Be(
            HttpStatusCode.OK, "the User role is half of the published role set");
        M3TestHarness.AssertRateLimitHeaders(userResponse, "GET 200 (User)");
        var userBody = await AccountTestHost.ReadJsonAsync(userResponse);
        userBody.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .Should().Equal([modelId], "the User caller sees exactly the allow-listed row");

        using var adminResponse = await M3TestHarness.SendAsync(
            host, HttpMethod.Get, "/v1/user/models?limit=100");
        adminResponse.StatusCode.Should().Be(HttpStatusCode.OK, "the Admin role is the other half");
        var adminBody = await AccountTestHost.ReadJsonAsync(adminResponse);
        adminBody.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .Should().Equal([modelId], "both roles read the identical allow-list");
    }

    // -----------------------------------------------------------------------
    // ?limit= (default 25, max 100 — §13.13, no pagination in M3)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Task scope table: <c>?limit=101</c> is a §13.12 400
    /// <c>/errors/validation</c> whose <c>errors[]</c> entry names
    /// <c>limit</c>, and the default (no query string at all) still answers
    /// 200 with the same reduced projection.
    /// </summary>
    [Fact]
    public async Task UserModels_LimitOverMax_Returns400_NamingLimit()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        await M3TestHarness.SeedRowAsync(
            factory, host.AdminId, $"{host.Marker} pickable", $"{host.Marker}-a");

        using var overMax = await M3TestHarness.SendAsync(
            host, HttpMethod.Get, "/v1/user/models?limit=101");
        overMax.StatusCode.Should().Be(HttpStatusCode.BadRequest, "§13.13 caps limit at 100");
        var problem = await AccountTestHost.ReadProblemAsync(overMax, HttpStatusCode.BadRequest);
        problem.GetProperty("type").GetString().Should().Be("/errors/validation");
        problem.GetProperty("errors").EnumerateArray().First().GetProperty("name").GetString()
            .Should().Be("limit", "the RFC 7807 errors[] entry names the offending field");
        problem.GetProperty("errors").EnumerateArray().First().GetProperty("reason").GetString()
            .Should().Contain("100", "the reason states the cap");

        using var @default = await M3TestHarness.SendAsync(host, HttpMethod.Get, "/v1/user/models");
        @default.StatusCode.Should().Be(
            HttpStatusCode.OK, "an absent limit falls back to the 25 default");
        var defaultBody = await AccountTestHost.ReadJsonAsync(@default);
        defaultBody.GetProperty("items").ValueKind.Should().Be(JsonValueKind.Array);
    }

    /// <summary>
    /// The cap is enforced on the RESULT too: with two pickable rows,
    /// <c>?limit=1</c> returns exactly one and <c>?limit=100</c> both — and
    /// the order is the deterministic <c>(created_at, id)</c> the picker
    /// renders in.
    /// </summary>
    [Fact]
    public async Task UserModels_Limit_CapsThePage_AndKeepsDeterministicOrder()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        var first = await M3TestHarness.SeedRowAsync(
            factory, host.AdminId, $"{host.Marker} one", $"{host.Marker}-a");
        var second = await M3TestHarness.SeedRowAsync(
            factory, host.AdminId, $"{host.Marker} two", $"{host.Marker}-b");

        using var capped = await M3TestHarness.SendAsync(
            host, HttpMethod.Get, "/v1/user/models?limit=1");
        capped.StatusCode.Should().Be(HttpStatusCode.OK);
        var cappedBody = await AccountTestHost.ReadJsonAsync(capped);
        cappedBody.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .Should().Equal([first], "Take(limit) keeps the first row of the (created_at, id) order");

        using var full = await M3TestHarness.SendAsync(
            host, HttpMethod.Get, "/v1/user/models?limit=100");
        full.StatusCode.Should().Be(HttpStatusCode.OK);
        var fullBody = await AccountTestHost.ReadJsonAsync(full);
        fullBody.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .Should().Equal([first, second],
                "two seeded rows, oldest first — the picker's stable rendering order");
    }

    // -----------------------------------------------------------------------
    // Lifecycle (one class instance per fact) + per-fact cleanup
    // -----------------------------------------------------------------------

    /// <summary>No per-test setup — <see cref="CreateHostAsync"/> is the setup seam.</summary>
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Blobs first, then the marker-keyed catalog rows. Audit rows are never touched.</summary>
    public async Task DisposeAsync()
    {
        foreach (var marker in _markers)
        {
            await M3TestHarness.CleanupAsync(postgres, marker);
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// A host with no mock: this suite seeds its rows directly, so it never
    /// probes and never needs an engine seam (still hermetic — no transport).
    /// </summary>
    private Task<M3TestHarness.M3Host> CreateHostAsync()
    {
        var marker = M3TestHarness.NewMarker();
        _markers.Add(marker);
        return M3TestHarness.CreateHostAsync(postgres, marker);
    }
}
