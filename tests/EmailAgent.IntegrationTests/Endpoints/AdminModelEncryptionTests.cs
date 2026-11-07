using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmailAgent.Api.Endpoints;
using EmailAgent.Api.Models;
using EmailAgent.Connectors.OpenAI;
using EmailAgent.Contracts.AgentEngines;
using EmailAgent.Core.Security;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using EmailAgent.Infrastructure.Security;
using EmailAgent.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EmailAgent.IntegrationTests.Endpoints;

// ---------------------------------------------------------------------------
// M3T3: the two task-named encryption tests. The host boots with ITS OWN
// random SYSTEM__KeyEncryptionKey (≥ 32 bytes, never a real secret) so the
// suite can prove three things at once: the blob is system-wrapped (not
// clear text), the configured key is the ONE key that opens it, and no
// response of any shape carries an api_key member. Marker prefix m3t3enc-
// (distinct from the endpoint suite's m3t3-) keys the per-test cleanup.
// ---------------------------------------------------------------------------

[Trait("Category", "M3T3")]
[Collection("Postgres")]
public class AdminModelEncryptionTests : IClassFixture<OpenAIMockFixture>, IAsyncLifetime
{
    /// <summary>Throwaway integration-test api_key (never a real credential).</summary>
    private const string ApiKey = "sk-m3t3enc-test-key";

    private const string Provider = "openai-compatible";

    /// <summary>The ApiTestFactory default — must NOT open this suite's blobs (proves the override).</summary>
    private const string FactoryDefaultSystemKey = "email-agent-integration-test-system-kek-0123456789";

    /// <summary>The fixed §13.13 ModelDto projection (snake_case wire names).</summary>
    private static readonly string[] DtoMembers =
    [
        "id",
        "display_name",
        "provider",
        "base_url",
        "model_name",
        "enabled",
        "tool_calling_probed_at",
        "tool_calling_probe_result",
        "created_by",
        "created_at",
        "price_per_1k_input_usd",
        "price_per_1k_output_usd",
    ];

    private readonly OpenAIMockFixture _fixture;
    private readonly PostgresFixture _postgres;
    private string? _marker;

    public AdminModelEncryptionTests(OpenAIMockFixture fixture, PostgresFixture postgres)
    {
        _fixture = fixture;
        _postgres = postgres;
    }

    // -----------------------------------------------------------------------
    // System-key binding.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Task row: <c>SYSTEM__KeyEncryptionKey = &lt;random 32+ bytes&gt;</c> —
    /// the ciphertext of an <c>encrypted_blobs</c> row does not contain the
    /// raw key bytes, unwraps with that key, and fails with any other.
    /// </summary>
    [Fact]
    public async Task PostApiKey_EncryptedUnderSystemKey_NotReadableAsPlaintext()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;

        var modelId = await CreateModelAsync(host, client);
        var row = (await FindRowAsync(factory, modelId))!;
        row.Should().NotBeNull("the 201 must be backed by a catalog row");
        row.ApiKeySecretRef.Should().NotBeNull();
        var blob = (await FindBlobAsync(factory, Guid.Parse(row.ApiKeySecretRef!)))!;
        blob.Should().NotBeNull("api_key_secret_ref must resolve to an encrypted_blobs row");

        blob.Purpose.Should().Be(
            EncryptedModelApiKeyStore.ModelApiKeyPurpose, "Q-E7: purpose='model_api_key'");
        blob.OwnerUserId.Should().BeNull("Q-E7: owner_user_id IS NULL — system-owned");
        blob.Nonce.Length.Should().Be(12, "AES-256-GCM nonce length");

        // Acceptance criterion: SELECT ciphertext … does NOT contain the raw key.
        ContainsBytes(blob.Ciphertext, Encoding.UTF8.GetBytes(ApiKey)).Should().BeFalse(
            "the api_key must be wrapped under the system key, never stored in clear");

        // Round-trips ONLY with this host's configured system key…
        var ownKeyService = CreateEncryption(host.SystemKey);
        Encoding.UTF8.GetString(ownKeyService.UnwrapWithSystemKey(blob.Ciphertext, blob.Nonce))
            .Should().Be(ApiKey, "SYSTEM__KeyEncryptionKey is the binding key of every model_api_key blob (Q-E7)");

        // …and never with any other key — including the factory default, which
        // also proves this host's UseSetting override actually took effect.
        var wrongKeyService = CreateEncryption(FactoryDefaultSystemKey);
        Action unwrapWithWrongKey = () => wrongKeyService.UnwrapWithSystemKey(blob.Ciphertext, blob.Nonce);
        unwrapWithWrongKey.Should().Throw<CryptographicException>(
            "a different system key must fail the GCM tag check");
    }

    // -----------------------------------------------------------------------
    // Typed-record proof on every response shape.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Task row: typed-record deserialization of every response — the deserialized
    /// property count matches <see cref="ModelDto"/> exactly (12), and no body
    /// (raw or typed) carries an api_key member or value.
    /// </summary>
    [Fact]
    public async Task AllResponses_DoNotContainApiKey_Property()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;
        var marker = host.Marker;

        // POST 201
        using var createResponse = await client.SendAsync(
            AdminTestHost.Request(HttpMethod.Post, "/v1/admin/models", host.Bearer, CreateBody(host, marker)));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var modelId = AssertModelDto(
            await createResponse.Content.ReadAsStringAsync(), "POST 201");

        // GET 200
        using var getResponse = await client.SendAsync(
            AdminTestHost.Request(HttpMethod.Get, $"/v1/admin/models/{modelId}", host.Bearer));
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertModelDto(await getResponse.Content.ReadAsStringAsync(), "GET 200");

        // GET list 200 (isolated by the per-test marker)
        using var listResponse = await client.SendAsync(AdminTestHost.Request(
            HttpMethod.Get,
            $"/v1/admin/models?filter%5Bdisplay_name_contains%5D={Uri.EscapeDataString(marker)}",
            host.Bearer));
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var listRaw = await listResponse.Content.ReadAsStringAsync();
        listRaw.Should().NotContain(ApiKey, "no response carries the api_key value");
        listRaw.Should().NotContain("api_key", "not even as a member name");
        using (var listDocument = JsonDocument.Parse(listRaw))
        {
            var items = listDocument.RootElement.GetProperty("items").EnumerateArray().ToList();
            items.Should().NotBeEmpty("this test's own create must be on the filtered list");
            foreach (var item in items)
            {
                AssertModelDto(item.GetRawText(), "GET list 200 item");
            }
        }

        // PATCH 200
        using var patchResponse = await client.SendAsync(AdminTestHost.Request(
            HttpMethod.Patch,
            $"/v1/admin/models/{modelId}",
            host.Bearer,
            new { display_name = $"{marker} renamed" }));
        patchResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertModelDto(await patchResponse.Content.ReadAsStringAsync(), "PATCH 200");

        // The published typed record itself: no credential member exists, so no
        // deserializer could ever bind one (task acceptance criterion).
        typeof(ModelDto).GetProperty("ApiKey").Should().BeNull(
            "ModelDto has no ApiKey property — typed-record proof");
        typeof(ModelDto).GetProperty("ApiKeySecretRef").Should().BeNull();
        typeof(ModelDto).GetProperty("EncryptedBlobId").Should().BeNull();
        typeof(ModelDto).GetProperties(BindingFlags.Public | BindingFlags.Instance).Length
            .Should().Be(DtoMembers.Length, "the typed record matches the wire projection exactly");
    }

    // -----------------------------------------------------------------------
    // Lifecycle (xUnit instantiates the class once per test) + per-test cleanup
    // -----------------------------------------------------------------------

    /// <summary>No per-test setup — <see cref="CreateHostAsync"/> is the setup seam.</summary>
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Per-test cleanup on the SHARED container database (marker-prefixed
    /// model_catalog rows + the key blobs they reference). Audit rows stay
    /// untouched — audit_log is append-only (the HMAC chain spans the shared
    /// table) and nothing in this suite asserts on audit rows.
    /// </summary>
    public async Task DisposeAsync()
    {
        var marker = _marker;
        if (marker is null)
        {
            return;
        }

        await AccountTestHost.MigrateAsync(_postgres);
        await using var db = new EmailAgentDbContext(
            new DbContextOptionsBuilder<EmailAgentDbContext>()
                .UseNpgsql(_postgres.ConnectionString)
                .Options);

        var rows = await db.ModelCatalog.AsNoTracking()
            .Where(model => model.ModelName.StartsWith(marker))
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
            .Where(model => model.ModelName.StartsWith(marker))
            .ExecuteDeleteAsync();
    }

    // -----------------------------------------------------------------------
    // Host + engine seam (same pattern as AdminModelEndpointsTests, plus the
    // per-host system key override).
    // -----------------------------------------------------------------------

    private sealed record ModelHost(
        WebApplicationFactory<Program> Factory,
        HttpClient Client,
        Guid AdminId,
        string Bearer,
        string Marker,
        string SystemKey,
        string BaseUrl);

    private async Task<ModelHost> CreateHostAsync()
    {
        var marker = $"m3t3enc-{Guid.NewGuid():N}";
        _marker = marker;

        // ≥ 32 bytes, unique per host, throwaway test material (never a real secret).
        var systemKey = $"m3t3enc-system-kek-{Guid.NewGuid():N}";

        await _fixture.SetScenarioAsync("probe-pass");

        var connectionString = await AccountTestHost.MigrateAsync(_postgres);
        var factory = ApiTestFactory.Create(connectionString)
            .WithWebHostBuilder(builder =>
            {
                // Last-writer-wins over ApiTestFactory's default
                // System:KeyEncryptionKey — test 1 proves the override took
                // effect in BOTH directions (this key opens the blob, the
                // factory default does not).
                builder.UseSetting("System:KeyEncryptionKey", systemKey);
                builder.ConfigureServices(services =>
                {
                    // M3T3 test seam: pin the probe's engine to the
                    // in-process OpenAIMock's TestServer client — no probe
                    // may ever reach a real provider (AGENTS.md constraint).
                    services.RemoveAll<Func<IAgentEngine>>();
                    Func<IAgentEngine> engine = () => new OpenAICompatibleAgentEngine(_fixture.Client);
                    services.AddSingleton(engine);
                });
            });
        var client = AccountTestHost.CreateClient(factory);
        var adminId = await AdminTestHost.SeedUserAsync(
            factory, $"{marker}@example.test", UserRole.Admin);
        var bearer = await AdminTestHost.BearerAsync(factory, adminId, "Admin");
        return new ModelHost(factory, client, adminId, bearer, marker, systemKey, _fixture.BaseUrl);
    }

    // -----------------------------------------------------------------------
    // Request/response helpers
    // -----------------------------------------------------------------------

    private static object CreateBody(ModelHost host, string marker) => new
    {
        display_name = $"{marker} model",
        provider = Provider,
        base_url = host.BaseUrl,
        model_name = marker,
        api_key = ApiKey,
    };

    /// <summary>POST asserts 201 + Location and returns the ModelDto id.</summary>
    private static async Task<Guid> CreateModelAsync(ModelHost host, HttpClient client)
    {
        using var response = await client.SendAsync(
            AdminTestHost.Request(HttpMethod.Post, "/v1/admin/models", host.Bearer, CreateBody(host, host.Marker)));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull("201 carries a Location header");
        var dto = await AccountTestHost.ReadJsonAsync(response);
        return dto.GetProperty("id").GetGuid();
    }

    /// <summary>
    /// One response of any catalog shape: no api_key VALUE anywhere, no
    /// api_key member name, and a typed ModelDto binding whose property count
    /// matches the wire projection exactly.
    /// </summary>
    private static Guid AssertModelDto(string raw, string what)
    {
        raw.Should().NotContain(ApiKey, $"{what}: the api_key value must never reach a response body");
        raw.Should().NotContain(
            "api_key", $"{what}: the projection has no api_key member at all");

        using var document = JsonDocument.Parse(raw);
        JsonElement dto = document.RootElement.Clone();
        dto.ValueKind.Should().Be(JsonValueKind.Object, $"{what}: a bare ModelDto object");
        dto.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo(
                DtoMembers,
                $"{what}: the fixed 12-member projection — no api_key, no api_key_secret_ref, no encrypted_blob_id");

        var typed = JsonSerializer.Deserialize(raw, AdminModelsJsonContext.Default.ModelDto);
        typed.Should().NotBeNull($"{what}: the published ModelDto must bind the body");
        typeof(ModelDto).GetProperties(BindingFlags.Public | BindingFlags.Instance).Length
            .Should().Be(
                dto.EnumerateObject().Count(),
                $"{what}: the deserialized property count matches ModelDto exactly");
        return typed!.Id;
    }

    /// <summary>byte-wise containment (ciphertext can never "contain" by accident of encoding).</summary>
    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        for (var offset = 0; offset + needle.Length <= haystack.Length; offset++)
        {
            if (haystack.AsSpan(offset, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }

    // -----------------------------------------------------------------------
    // Database + crypto helpers
    // -----------------------------------------------------------------------

    private static async Task<ModelCatalogEntry?> FindRowAsync(
        WebApplicationFactory<Program> factory, Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return await db.ModelCatalog.AsNoTracking().SingleOrDefaultAsync(model => model.Id == id);
    }

    private static async Task<EncryptedBlob?> FindBlobAsync(
        WebApplicationFactory<Program> factory, Guid blobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return await db.EncryptedBlobs.AsNoTracking()
            .SingleOrDefaultAsync(blob => blob.Id == blobId);
    }

    /// <summary>The real M1T3 service bound to a given key (the Q-E7 unwrap pair).</summary>
    private static IEncryptionService CreateEncryption(string key)
        => new Argon2idAesGcmEncryptionService(
            Options.Create(new EncryptionOptions { KeyEncryptionKey = key }),
            NullLogger<Argon2idAesGcmEncryptionService>.Instance);
}
