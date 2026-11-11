using System.Net;
using System.Text;
using System.Text.Json;
using EmailAgent.Api.Models;
using EmailAgent.Connectors.OpenAI;
using EmailAgent.Contracts.AgentEngines;
using EmailAgent.Core.Audit;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using EmailAgent.Infrastructure.Plugins;
using EmailAgent.IntegrationTests.Endpoints;
using EmailAgent.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace EmailAgent.IntegrationTests.M3EndToEnd;

/// <summary>
/// M3 end-to-end suite entry point (M3T7): the collection is explicitly
/// non-parallel so these facts own their Postgres container end to end — no
/// other test may interleave model_catalog or audit_log rows with the
/// smoke-equivalent flows below.
/// </summary>
[CollectionDefinition("M3E2E", DisableParallelization = true)]
public sealed class M3E2ECollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "M3E2E";
}

// ---------------------------------------------------------------------------
// M3T7: the milestone's own exit-criterion facts (task §Scope "Integration
// test"), one [Fact] per smoke flow of scripts/smoke-m3.sh, against Testcontainers
// Postgres + the real host with the M3T1 in-process OpenAIMock standing in for
// every provider call — no socket, no real provider, no literal that is not a
// test fixture (AGENTS.md test constraints).
//
// Isolation rules (M2E2E / M3T3 / M3T6 parity):
//  - one unique `m3t7-<guid>` marker per host: the model_name prefix every
//    DisposeAsync cleanup keys its catalog-row/blob deletion on;
//  - audit rows are NEVER deleted (audit_log is append-only and the HMAC chain
//    spans the shared table) — every audit assertion correlates by marker or
//    model id;
//  - one OpenAIMockFixture per class with the scenario pinned per step;
//  - each fact builds its OWN host, so the 60/min authenticated budget and the
//    plugin registry start fresh per fact.
// ---------------------------------------------------------------------------

[Trait("Category", "M3EndToEnd")]
[Collection(M3E2ECollection.Name)]
public sealed class M3EndToEndTests(PostgresFixture postgres, OpenAIMockFixture mock)
    : IClassFixture<OpenAIMockFixture>, IAsyncLifetime
{
    private readonly List<string> _markers = [];

    // -----------------------------------------------------------------------
    // Smoke flow 1 — admin adds the passing model (M3 exit criterion 1)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Smoke flow 1: a model pointed at the passing mock answers
    /// <c>tool_calling_probe_result='Pass'</c> inline, persists as a
    /// <c>model_catalog</c> row + a wrapped <c>model_api_key</c> blob, and
    /// never echoes the key back through any read surface.
    /// </summary>
    [Fact]
    public async Task Admin_AddsPassingModel_ProbePassesInline_ModelPersistsWithoutApiKey()
    {
        var host = await CreateHostAsync("probe-pass");
        await using var factory = host.Factory;
        var requestsBefore = TotalRequests();

        using var response = await M3TestHarness.SendAsync(
            host,
            HttpMethod.Post,
            "/v1/admin/models",
            M3TestHarness.CreateBody(host.Marker, mock.BaseUrl));

        response.StatusCode.Should().Be(HttpStatusCode.Created, "a passing probe yields 201");
        TotalRequests().Should().Be(
            requestsBefore + 1,
            "the probe ran against the in-process OpenAIMock — never a real provider");
        M3TestHarness.AssertRateLimitHeaders(response, "POST 201");

        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain(M3TestHarness.ApiKey, "the API key must never reach a response body");
        using var document = JsonDocument.Parse(raw);
        var dto = document.RootElement.Clone();
        dto.GetProperty("tool_calling_probe_result").GetString().Should().Be("Pass");
        dto.GetProperty("enabled").GetBoolean().Should().BeTrue();
        dto.EnumerateObject().Select(property => property.Name)
            .Should().NotContain("api_key", "the admin projection has no key member either");
        var modelId = dto.GetProperty("id").GetGuid();

        // Row + system-owned key blob, ciphertext never in the clear.
        var row = await M3TestHarness.FindRowAsync(factory, modelId);
        row.Should().NotBeNull("the 201 must be backed by a model_catalog row");
        row!.ToolCallingProbeResult.Should().Be(ToolCallingProbeResult.Pass);
        row.CreatedBy.Should().Be(host.AdminId);
        row.ApiKeySecretRef.Should().NotBeNull();
        var blob = await M3TestHarness.FindBlobAsync(factory, Guid.Parse(row.ApiKeySecretRef!));
        blob.Should().NotBeNull("api_key_secret_ref must resolve to an encrypted_blobs row");
        blob!.Purpose.Should().Be(EncryptedModelApiKeyStore.ModelApiKeyPurpose);
        blob.OwnerUserId.Should().BeNull("Q-E7: system-owned key material");
        M3TestHarness.ContainsBytes(blob.Ciphertext, Encoding.UTF8.GetBytes(M3TestHarness.ApiKey))
            .Should().BeFalse("the ciphertext must not hold the raw api_key bytes");

        // Smoke flow 1 steps 4 + 5: the row shows up in the admin list and the
        // by-id read still carries no api_key member.
        var filter = $"filter%5Bdisplay_name_contains%5D={Uri.EscapeDataString(host.Marker)}";
        using var list = await M3TestHarness.SendAsync(
            host, HttpMethod.Get, $"/v1/admin/models?limit=100&{filter}");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var listRaw = await list.Content.ReadAsStringAsync();
        listRaw.Should().NotContain(M3TestHarness.ApiKey);
        using (var listDocument = JsonDocument.Parse(listRaw))
        {
            listDocument.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("id").GetGuid())
                .Should().Contain(modelId, "the created row appears in GET /v1/admin/models");
        }

        using var get = await M3TestHarness.SendAsync(
            host, HttpMethod.Get, $"/v1/admin/models/{modelId}");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var getRaw = await get.Content.ReadAsStringAsync();
        getRaw.Should().NotContain(M3TestHarness.ApiKey);
        getRaw.Should().NotContain("api_key", "GET by id never returns a key member");
    }

    // -----------------------------------------------------------------------
    // Smoke flow 2 — admin adds the failing model
    // -----------------------------------------------------------------------

    /// <summary>
    /// Smoke flow 2: a probe failure is a §13.12 <c>400
    /// /errors/model-probe-failed</c> with a clamped excerpt, writes NO
    /// catalog row and NO key blob, and still leaves the fail-only audit row
    /// with <c>target_id IS NULL</c>.
    /// </summary>
    [Fact]
    public async Task Admin_AddsFailingModel_ProbeFailsInline_NoRowWritten()
    {
        var host = await CreateHostAsync("probe-fail");
        await using var factory = host.Factory;
        var requestsBefore = TotalRequests();
        var blobsBefore = await M3TestHarness.CountModelKeyBlobsAsync(factory);

        using var response = await M3TestHarness.SendAsync(
            host,
            HttpMethod.Post,
            "/v1/admin/models",
            M3TestHarness.CreateBody(host.Marker, mock.BaseUrl));

        var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.BadRequest);
        problem.GetProperty("type").GetString().Should().Be("/errors/model-probe-failed");
        TotalRequests().Should().Be(
            requestsBefore + 1, "the probe ran BEFORE any write (SQ-M3-11)");
        var detail = problem.GetProperty("detail").GetString();
        detail.Should().NotBeNullOrWhiteSpace("detail carries the probe's response excerpt");
        Encoding.UTF8.GetByteCount(detail!)
            .Should().BeLessOrEqualTo(256, "SQ-M3-3 clamps the excerpt to 256 UTF-8 bytes");
        detail.Should().NotContain(
            M3TestHarness.ApiKey, "the excerpt must never echo the api_key value");

        (await M3TestHarness.CountModelsAsync(factory, host.Marker)).Should().Be(
            0, "a failed probe must not write a model_catalog row");
        (await M3TestHarness.CountModelKeyBlobsAsync(factory)).Should().Be(
            blobsBefore, "a failed probe must not write an encrypted_blobs row");

        var failRows = await M3TestHarness.AuditMatchingAsync(
            factory, AuditActions.ModelProbeFailed, host.Marker);
        failRows.Should().ContainSingle("exactly one probe-fail row correlates to this request");
        failRows[0].TargetId.Should().BeNull("the POST path has no catalog row yet (SQ-M3-11)");
        failRows[0].Hmac.Length.Should().Be(32, "audit rows are HMAC-chained (Q-O2)");
        failRows[0].MetadataJson.Should().NotContain(M3TestHarness.ApiKey);
    }

    // -----------------------------------------------------------------------
    // Smoke flow 3 — admin runs test-completion (M3 exit criterion 2)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Smoke flow 3: the manual test-completion tool round-trips the prompt
    /// through the mock's <c>happy</c> scenario and returns the fixed
    /// 7-member §13.12 projection with tokens and the price-derived cost.
    /// </summary>
    [Fact]
    public async Task Admin_RunsTestCompletion_RoundTripsPrompt_AndRecordsTokens()
    {
        var host = await CreateHostAsync("probe-pass");
        await using var factory = host.Factory;
        var created = await M3TestHarness.CreateModelAsync(
            host, M3TestHarness.CreateBody(host.Marker, mock.BaseUrl));
        var modelId = created.GetProperty("id").GetGuid();

        // Smoke step 3.1: switch the very same instance to `happy` — engine-
        // driven calls carry no X-Test-Scenario header, the active scenario is
        // how the smoke (and here, the test) steers the instance.
        await mock.SetScenarioAsync("happy");
        var requestsBefore = TotalRequests();

        using var response = await M3TestHarness.SendAsync(
            host,
            HttpMethod.Post,
            $"/v1/admin/models/{modelId}/test-completion",
            new { system_prompt = M3TestHarness.SystemPrompt, user_prompt = M3TestHarness.UserPrompt });

        response.StatusCode.Should().Be(HttpStatusCode.OK, "a completed call is §13.12 200");
        TotalRequests().Should().Be(requestsBefore + 1, "exactly one engine call ran");
        var body = await AccountTestHost.ReadJsonAsync(response);
        body.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            M3TestHarness.TestCompletionMembers, "the fixed 7-member response projection");
        body.GetProperty("response_text").GetString().Should().Be(
            "hello world", "the mock's recorded completion content");
        body.GetProperty("tool_calls").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("prompt_tokens").GetInt32().Should().Be(5);
        body.GetProperty("completion_tokens").GetInt32().Should().Be(2);
        body.GetProperty("total_tokens").GetInt32().Should().Be(7);
        body.GetProperty("cost_usd").GetDecimal().Should().Be(
            0.00012m, "(5/1000)*0.02 + (2/1000)*0.01 — the seeded prices");
        body.GetProperty("duration_ms").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain(M3TestHarness.ApiKey, "the §13.12 surface has no credential member");
        raw.Should().NotContain("api_key");
    }

    /// <summary>
    /// Smoke flow 3 step 3: the <c>test_model_completion</c> audit row carries
    /// the SQ-M3-4 field set — a 64-char lowercase-hex <c>prompt_sha256</c> and
    /// NEVER the prompt text itself.
    /// </summary>
    [Fact]
    public async Task Admin_TestCompletion_AuditRow_ContainsPromptHash_NotPromptText()
    {
        var host = await CreateHostAsync("probe-pass");
        await using var factory = host.Factory;
        var created = await M3TestHarness.CreateModelAsync(
            host, M3TestHarness.CreateBody(host.Marker, mock.BaseUrl));
        var modelId = created.GetProperty("id").GetGuid();

        await mock.SetScenarioAsync("happy");
        using var response = await M3TestHarness.SendAsync(
            host,
            HttpMethod.Post,
            $"/v1/admin/models/{modelId}/test-completion",
            new
            {
                system_prompt = M3TestHarness.SystemPrompt,
                user_prompt = M3TestHarness.UserPrompt,
            });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var row = await M3TestHarness.SingleAuditAsync(
            factory, AuditActions.TestModelCompletion, modelId);
        row.TargetType.Should().Be("model_catalog");
        row.ActorUserId.Should().Be(host.AdminId);
        row.ActorRole.Should().Be("Admin");
        row.Hmac.Length.Should().Be(32, "audit rows are HMAC-chained (Q-O2)");
        row.MetadataJson.Should().NotContain(
            M3TestHarness.UserPrompt, "the audit row stores a hash, never the prompt text");

        using var metadata = JsonDocument.Parse(row.MetadataJson!);
        var root = metadata.RootElement;
        var promptHash = root.GetProperty("prompt_sha256").GetString();
        promptHash.Should().NotBeNullOrWhiteSpace();
        promptHash!.Should().HaveLength(64, "SHA-256 as lowercase hex");
        promptHash.Should().MatchRegex("^[0-9a-f]{64}$");
        promptHash.Should().Be(ExpectedPromptHash());
        root.GetProperty("prompt_tokens").GetInt32().Should().Be(5);
        root.GetProperty("completion_tokens").GetInt32().Should().Be(2);
        root.GetProperty("total_tokens").GetInt32().Should().Be(7);
        root.GetProperty("cost_usd").GetDecimal().Should().Be(0.00012m);
        root.GetProperty("model_id").GetGuid().Should().Be(modelId);
    }

    // -----------------------------------------------------------------------
    // Smoke flow 5 — admin deletes the passing model
    // -----------------------------------------------------------------------

    /// <summary>
    /// Smoke flow 5: DELETE answers 204 and removes the catalog row together
    /// with the key blob it referenced; a never-inserted id is a 404
    /// <c>/errors/model-not-found</c>.
    /// </summary>
    [Fact]
    public async Task Admin_DeletesModel_CascadesEncryptedBlob()
    {
        var host = await CreateHostAsync("probe-pass");
        await using var factory = host.Factory;
        var created = await M3TestHarness.CreateModelAsync(
            host, M3TestHarness.CreateBody(host.Marker, mock.BaseUrl));
        var modelId = created.GetProperty("id").GetGuid();
        var row = await M3TestHarness.FindRowAsync(factory, modelId);
        row.Should().NotBeNull("the created row must exist before DELETE");
        var blobId = Guid.Parse(row!.ApiKeySecretRef!);

        using var response = await M3TestHarness.SendAsync(
            host, HttpMethod.Delete, $"/v1/admin/models/{modelId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, "DELETE answers 204");
        (await M3TestHarness.FindRowAsync(factory, modelId)).Should().BeNull(
            "the catalog row is gone");
        (await M3TestHarness.FindBlobAsync(factory, blobId)).Should().BeNull(
            "the referenced encrypted_blobs row is removed in the same transaction");

        var auditRow = await M3TestHarness.SingleAuditAsync(
            factory, AuditActions.DeleteModel, modelId);
        auditRow.Hmac.Length.Should().Be(32, "audit rows are HMAC-chained (Q-O2)");
        auditRow.TargetType.Should().Be("model_catalog");
        auditRow.ActorUserId.Should().Be(host.AdminId);
        auditRow.MetadataJson.Should().NotContain(M3TestHarness.ApiKey);
        auditRow.MetadataJson.Should().NotContain("api_key");

        // Smoke flow 5 step 2: deleting the id of a model that was never
        // inserted (the failing create wrote no row) is a 404, not a 500.
        using var missing = await M3TestHarness.SendAsync(
            host, HttpMethod.Delete, $"/v1/admin/models/{Guid.NewGuid()}");
        var problem = await AccountTestHost.ReadProblemAsync(missing, HttpStatusCode.NotFound);
        problem.GetProperty("type").GetString().Should().Be("/errors/model-not-found");
    }

    // -----------------------------------------------------------------------
    // Lifecycle (one class instance per fact) + per-fact cleanup
    // -----------------------------------------------------------------------

    /// <summary>No per-test setup — <see cref="CreateHostAsync"/> is the setup seam.</summary>
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Per-fact cleanup on this collection's container database: the
    /// model_catalog rows created here (marker prefix) plus the key blobs they
    /// reference, blobs first (the FK direction) then rows. Audit rows are
    /// deliberately left in place — audit_log is append-only and the HMAC
    /// chain spans the table, so deletion would break every chain assertion
    /// including AuditChainTests.
    /// </summary>
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
    /// One host per fact: migrated collection Postgres, a seeded Admin with a
    /// real issuer-minted token, a fresh unique marker and this class's
    /// OpenAIMock pinned to the asked scenario.
    /// </summary>
    private Task<M3TestHarness.M3Host> CreateHostAsync(string scenario)
    {
        var marker = M3TestHarness.NewMarker();
        _markers.Add(marker);
        return M3TestHarness.CreateHostAsync(postgres, marker, mock, scenario);
    }

    /// <summary>Engine round-trips against this fixture's mock, summed over scenarios.</summary>
    private int TotalRequests() => mock.RequestCounts.Values.Sum();

    /// <summary>
    /// Independent re-derivation of SQ-M3-4's prompt hash — never the
    /// implementation's own output.
    /// </summary>
    private static string ExpectedPromptHash()
        => Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"{M3TestHarness.SystemPrompt}\n{M3TestHarness.UserPrompt}")))
            .ToLowerInvariant();
}

// ---------------------------------------------------------------------------
// Shared M3T7 harness: host/engine seam, request builders, the §13.14 header
// assertion, the model/blob/audit readers and the marker-keyed cleanup. It
// lives here (with the collection definition) so M3T7's exact file list stays
// M3EndToEndTests / StubAgentPageTests / UserModelEndpointsTests /
// AuditChainTests.
// ---------------------------------------------------------------------------

/// <summary>Static helpers shared by the four M3EndToEnd test classes.</summary>
internal static class M3TestHarness
{
    public const string Provider = "openai-compatible";

    /// <summary>Throwaway integration-test api_key (never a real credential).</summary>
    public const string ApiKey = "sk-test-redacted-m3t7";

    public const string SystemPrompt = "You are helpful.";

    public const string UserPrompt = "Say hello.";

    /// <summary>The fixed §13.12 test-completion 200 projection.</summary>
    public static readonly string[] TestCompletionMembers =
    [
        "response_text",
        "tool_calls",
        "prompt_tokens",
        "completion_tokens",
        "total_tokens",
        "cost_usd",
        "duration_ms",
    ];

    /// <summary>A per-fact host: the factory, its client, the seeded Admin and the marker.</summary>
    public sealed record M3Host(
        WebApplicationFactory<Program> Factory,
        HttpClient Client,
        Guid AdminId,
        string Bearer,
        string Marker,
        string Session);

    /// <summary>One unique list-isolation + cleanup key per host.</summary>
    public static string NewMarker() => $"m3t7-{Guid.NewGuid():N}";

    /// <summary>
    /// Migrated Postgres + a host whose probe and test-completion both ride the
    /// in-process OpenAIMock (no socket, no real provider). The engine seam is
    /// set twice because M3 resolves it twice: the catalog probe through
    /// <c>Func&lt;IAgentEngine&gt;</c> and the test-completion tool through
    /// <c>IPluginRegistry</c> — the latter only AFTER the host has booted,
    /// because PluginLoader.StartAsync awaits a ReloadAsync that clears the
    /// registry.
    /// </summary>
    /// <param name="mock">The mock fixture, or <see langword="null"/> for hosts that never call out.</param>
    /// <param name="scenario">The mock's active scenario before the host can probe.</param>
    public static async Task<M3Host> CreateHostAsync(
        PostgresFixture postgres,
        string marker,
        OpenAIMockFixture? mock = null,
        string scenario = "probe-pass")
    {
        if (mock is not null)
        {
            await mock.SetScenarioAsync(scenario);
        }

        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        var factory = ApiTestFactory.Create(connectionString);
        if (mock is not null)
        {
            var fixture = mock;
            factory = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<Func<IAgentEngine>>();
                Func<IAgentEngine> engine = () => new OpenAICompatibleAgentEngine(fixture.Client);
                services.AddSingleton(engine);
            }));
        }

        var client = AccountTestHost.CreateClient(factory); // boots the host (hosted services included)

        if (mock is not null)
        {
            factory.Services.GetRequiredService<IPluginRegistry>().RegisterAgentEngine(
                Provider, new OpenAICompatibleAgentEngine(mock.Client));
        }

        var adminId = await AdminTestHost.SeedUserAsync(
            factory, $"{marker}@example.test", UserRole.Admin);
        var bearer = await AdminTestHost.BearerAsync(factory, adminId, "Admin");

        // M5T1: /v1/user/* needs an unlocked crypto session alongside the JWT
        // (POST /v1/account/unlock with a test-minted DEK, no production service).
        var session = await SourcesTestHost.UnlockAsync(client, bearer, SourcesTestHost.NewDek());
        return new M3Host(factory, client, adminId, bearer, marker, session);
    }

    /// <summary>An authenticated request against a host's admin/user routes (X-Crypto-Session rides along for /v1/user/*).</summary>
    public static Task<HttpResponseMessage> SendAsync(
        M3Host host, HttpMethod method, string path, object? body = null)
        => host.Client.SendAsync(AdminTestHost.Request(method, path, host.Bearer, body, host.Session));

    /// <summary>
    /// The §13.13 admin create body. Prices are pinned to the values smoke
    /// flow 3's cost assertion derives from (0.02 / 0.01 → 0.00012 for 5/2).
    /// </summary>
    public static object CreateBody(
        string marker,
        string baseUrl,
        string? displayName = null,
        string? modelName = null,
        bool enabled = true)
        => new
        {
            display_name = displayName ?? $"{marker} model",
            provider = Provider,
            base_url = baseUrl,
            model_name = modelName ?? marker,
            api_key = ApiKey,
            price_per_1k_input_usd = 0.02m,
            price_per_1k_output_usd = 0.01m,
            enabled,
        };

    /// <summary>POST /v1/admin/models (asserts 201) and returns the key-free ModelDto.</summary>
    public static async Task<JsonElement> CreateModelAsync(M3Host host, object body)
    {
        using var response = await SendAsync(host, HttpMethod.Post, "/v1/admin/models", body);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull("201 carries a Location header");
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain(ApiKey, "the API key must never reach a response body");
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    /// <summary>§13.14: every authenticated response carries X-RateLimit-Limit/-Remaining/-Reset.</summary>
    public static void AssertRateLimitHeaders(HttpResponseMessage response, string what)
    {
        response.Headers.Contains("X-RateLimit-Limit").Should().BeTrue($"{what}: X-RateLimit-Limit (§13.14)");
        int.TryParse(response.Headers.GetValues("X-RateLimit-Limit").Single(), out var limit)
            .Should().BeTrue("X-RateLimit-Limit is an integer");
        limit.Should().Be(60, "the M1T4 authenticated per-user budget");
        response.Headers.Contains("X-RateLimit-Remaining").Should().BeTrue($"{what}: X-RateLimit-Remaining");
        int.TryParse(response.Headers.GetValues("X-RateLimit-Remaining").Single(), out var remaining)
            .Should().BeTrue("X-RateLimit-Remaining is an integer");
        remaining.Should().BeGreaterThanOrEqualTo(0);
        response.Headers.Contains("X-RateLimit-Reset").Should().BeTrue($"{what}: X-RateLimit-Reset");
        long.TryParse(response.Headers.GetValues("X-RateLimit-Reset").Single(), out var reset)
            .Should().BeTrue("X-RateLimit-Reset is unix seconds");
        reset.Should().BeGreaterThan(0);
    }

    /// <summary>Byte-sequence containment (ciphertext checks — never string decoding).</summary>
    public static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (var offset = 0; offset <= haystack.Length - needle.Length; offset++)
        {
            if (haystack.AsSpan(offset, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }

    // -----------------------------------------------------------------------
    // Database readers/writers (shared Postgres container)
    // -----------------------------------------------------------------------

    public static async Task<ModelCatalogEntry?> FindRowAsync(
        WebApplicationFactory<Program> factory, Guid modelId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return await db.ModelCatalog.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == modelId);
    }

    public static async Task<EncryptedBlob?> FindBlobAsync(
        WebApplicationFactory<Program> factory, Guid blobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return await db.EncryptedBlobs.AsNoTracking()
            .SingleOrDefaultAsync(blob => blob.Id == blobId);
    }

    /// <summary>Catalog rows whose <c>model_name</c> starts with the marker.</summary>
    public static async Task<int> CountModelsAsync(
        WebApplicationFactory<Program> factory, string markerPrefix)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return await db.ModelCatalog.AsNoTracking()
            .CountAsync(row => row.ModelName.StartsWith(markerPrefix));
    }

    /// <summary>Total <c>purpose='model_api_key'</c> rows — a probe-fail must not move it.</summary>
    public static async Task<int> CountModelKeyBlobsAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return await db.EncryptedBlobs.AsNoTracking()
            .CountAsync(blob => blob.Purpose == EncryptedModelApiKeyStore.ModelApiKeyPurpose);
    }

    /// <summary>
    /// Direct DbContext seed of a catalog row + wrapped key blob: the M3T3
    /// <c>SeedRowAsync</c> shape the filter/role/limit and picker facts need,
    /// without paying for a probe round trip.
    /// </summary>
    /// <param name="baseUrl">Never contacted — seeded rows are read, not probed.</param>
    /// <param name="createdBy">The admin the row belongs to (FK).</param>
    public static async Task<Guid> SeedRowAsync(
        WebApplicationFactory<Program> factory,
        Guid createdBy,
        string displayName,
        string modelName,
        bool enabled = true,
        ToolCallingProbeResult probeResult = ToolCallingProbeResult.Pass,
        string baseUrl = "http://m3t7-openai-mock.invalid/v1")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<EncryptedModelApiKeyStore>();
        var blobId = await store.WriteAsync(ApiKey, CancellationToken.None);

        var id = Guid.NewGuid();
        db.ModelCatalog.Add(new ModelCatalogEntry
        {
            Id = id,
            DisplayName = displayName,
            Provider = Provider,
            BaseUrl = baseUrl,
            ModelName = modelName,
            Enabled = enabled,
            CreatedBy = createdBy,
            CreatedAt = DateTimeOffset.UtcNow,
            ApiKeySecretRef = blobId.ToString(),
            PricePer1kInputUsd = 0.02m,
            PricePer1kOutputUsd = 0.01m,
            ToolCallingProbeResult = probeResult,
            ToolCallingProbedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>The single audit row of <paramref name="action"/> targeting this model id.</summary>
    public static async Task<AuditLog> SingleAuditAsync(
        WebApplicationFactory<Program> factory, string action, Guid modelId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var rows = await db.AuditLog.AsNoTracking()
            .Where(row => row.Action == action && row.TargetId == modelId.ToString())
            .OrderBy(row => row.Id)
            .ToListAsync();
        rows.Should().ContainSingle(
            $"exactly one {action} row must target {modelId} — found {rows.Count}");
        return rows[0];
    }

    /// <summary>Audit rows of <paramref name="action"/> whose metadata mentions the marker.</summary>
    public static async Task<List<AuditLog>> AuditMatchingAsync(
        WebApplicationFactory<Program> factory, string action, string marker)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var rows = await db.AuditLog.AsNoTracking()
            .Where(row => row.Action == action)
            .OrderBy(row => row.Id)
            .ToListAsync();
        return
        [
            .. rows
                .Where(row => (row.MetadataJson ?? string.Empty)
                    .Contains(marker, StringComparison.Ordinal))
        ];
    }

    /// <summary>
    /// Marker-keyed cleanup: key blobs first, then the catalog rows that
    /// referenced them. Audit rows are never touched (append-only HMAC chain).
    /// </summary>
    public static async Task CleanupAsync(PostgresFixture postgres, string markerPrefix)
    {
        await AccountTestHost.MigrateAsync(postgres);
        await using var db = new EmailAgentDbContext(
            new DbContextOptionsBuilder<EmailAgentDbContext>()
                .UseNpgsql(postgres.ConnectionString)
                .Options);

        var rows = await db.ModelCatalog.AsNoTracking()
            .Where(model => model.ModelName.StartsWith(markerPrefix))
            .Select(model => new { model.ApiKeySecretRef })
            .ToListAsync();

        var blobIds = new List<Guid>(rows.Count);
        foreach (var row in rows)
        {
            if (row.ApiKeySecretRef is { } reference && Guid.TryParse(reference, out var blobId))
            {
                blobIds.Add(blobId);
            }
        }

        if (blobIds.Count > 0)
        {
            await db.EncryptedBlobs
                .Where(blob => blobIds.Contains(blob.Id))
                .ExecuteDeleteAsync();
        }

        await db.ModelCatalog
            .Where(model => model.ModelName.StartsWith(markerPrefix))
            .ExecuteDeleteAsync();
    }
}
