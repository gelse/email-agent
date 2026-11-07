using System.Text;
using System.Text.Json;
using EmailAgent.Api.Config;
using EmailAgent.Api.Models;
using EmailAgent.Connectors.OpenAI;
using EmailAgent.Connectors.OpenAI.Http;
using EmailAgent.Core.Audit;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Audit;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using EmailAgent.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EmailAgent.IntegrationTests.Models;

/// <summary>
/// M3T2 integration coverage (the task's nine named tests): the probe against
/// M3T1's engine driving the in-process <see cref="OpenAIMock"/>
/// (scenarios <c>probe-pass</c> / <c>probe-fail</c>) with a real
/// <see cref="AuditLogger"/> on Testcontainers Postgres — no socket, no real
/// provider (AGENTS.md test constraint).
/// </summary>
/// <remarks>
/// Every test uses a unique <c>model_name</c>, which isolates the
/// <c>(base_url, model_name)</c> cache on the shared container database and
/// doubles as the unique marker audit assertions filter on (the container's
/// <c>audit_log</c> is shared with every other test class, so rows are found
/// by correlation — never by "the only row").
/// </remarks>
[Trait("Category", "M3T2")]
[Collection("Postgres")]
public class ModelProbeServiceTests : IClassFixture<OpenAIMockFixture>, IAsyncLifetime
{
    /// <summary>Throwaway integration-test HMAC key (same literal family as ApiTestFactory — never a real secret).</summary>
    private const string AuditHmacKey = "m1t4-integration-test-audit-hmac-key-0123456789";

    private const string ApiKey = "sk-test-redacted";

    /// <summary>
    /// Unique marker prefix for every <c>model_name</c> this class seeds
    /// (<c>m3t2-&lt;guid&gt;</c>): isolates the <c>(base_url, model_name)</c>
    /// cache on the shared container database, doubles as the correlation
    /// marker for audit assertions, and keys the per-test cleanup below.
    /// </summary>
    private const string MarkerPrefix = "m3t2-";

    private static readonly SemaphoreSlim MigrationGate = new(1, 1);
    private static string? _migratedConnectionString;

    private readonly OpenAIMockFixture _fixture;
    private readonly PostgresFixture _postgres;

    public ModelProbeServiceTests(OpenAIMockFixture fixture, PostgresFixture postgres)
    {
        _fixture = fixture;
        _postgres = postgres;
    }

    // -----------------------------------------------------------------------
    // Pass path (probe-pass scenario).
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ProbeAsync_HappyPath_ReturnsPass()
    {
        var connectionString = await MigrateAsync();
        await using var db = CreateDb(connectionString);
        await _fixture.SetScenarioAsync("probe-pass");
        var modelName = UniqueModel();
        var engine = new OpenAICompatibleAgentEngine(_fixture.Client);
        var service = CreateService(db, engine);
        var config = ConfigFor(_fixture.BaseUrl, modelName);
        var before = TotalRequests();

        var result = await service.ProbeAsync(config, null, CancellationToken.None);

        result.Outcome.Should().Be(ProbeOutcome.Pass, "probe-pass returns tool_calls=[{ping}] (SQ-M3-3)");
        result.Reason.Should().BeNull();
        result.CachedFrom.Should().BeNull();
        result.RequestExcerpt.Should().Be(CanonicalRequestExcerpt(modelName));
        TotalRequests().Should().Be(before + 1, "exactly one engine round-trip ran");

        using var body = JsonDocument.Parse(result.ResponseExcerpt);
        var message = body.RootElement.GetProperty("choices")[0].GetProperty("message");
        message.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name")
            .GetString().Should().Be("ping");
        body.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt32().Should().Be(4);
        body.RootElement.GetProperty("usage").GetProperty("completion_tokens").GetInt32().Should().Be(6);
    }

    [Fact]
    public async Task ProbeAsync_HappyPath_EmitsNoProbeAuditRow()
    {
        var connectionString = await MigrateAsync();
        await using var db = CreateDb(connectionString);
        await _fixture.SetScenarioAsync("probe-pass");
        var modelName = UniqueModel();
        var service = CreateService(db, new OpenAICompatibleAgentEngine(_fixture.Client));

        var result = await service.ProbeAsync(ConfigFor(_fixture.BaseUrl, modelName), null, CancellationToken.None);

        result.Outcome.Should().Be(ProbeOutcome.Pass);
        Mentioning(await ProbeFailRowsAsync(db), modelName).Should().BeEmpty(
            "pass outcomes leave the audit trail to M3T3's CreateModel/UpdateModel metadata (Q-O17)");
    }

    // -----------------------------------------------------------------------
    // Fail path (probe-fail scenario).
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ProbeAsync_ProbeFail_ReturnsFail_WithResponseExcerpt()
    {
        var connectionString = await MigrateAsync();
        await using var db = CreateDb(connectionString);
        await _fixture.SetScenarioAsync("probe-fail");
        var modelName = UniqueModel();
        var service = CreateService(db, new OpenAICompatibleAgentEngine(_fixture.Client));
        var before = TotalRequests();

        var result = await service.ProbeAsync(ConfigFor(_fixture.BaseUrl, modelName), null, CancellationToken.None);

        result.Outcome.Should().Be(ProbeOutcome.Fail);
        result.Reason.Should().Be("no_tool_calls");
        TotalRequests().Should().Be(before + 1);
        Encoding.UTF8.GetByteCount(result.ResponseExcerpt).Should().BeLessOrEqualTo(
            256, "SQ-M3-3 clamps the excerpt to 256 UTF-8 bytes");

        // The excerpt is the reconstructed body (never the raw fixture — that
        // one embeds the key marker): first 256 bytes of the reconstruction.
        result.ResponseExcerpt.Should().Be(Utf8Excerpt.Truncate(ExpectedFailResponse(), 256));
        using var body = JsonDocument.Parse(result.ResponseExcerpt);
        var message = body.RootElement.GetProperty("choices")[0].GetProperty("message");
        message.GetProperty("content").GetString().Should().Be("I can't do tool calls");
        message.GetProperty("tool_calls").GetArrayLength().Should().Be(0);
        body.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt32().Should().Be(4);
        body.RootElement.GetProperty("usage").GetProperty("completion_tokens").GetInt32().Should().Be(9);
    }

    [Fact]
    public async Task ProbeAsync_ProbeFail_EmitsModelProbeFailedAuditRow_WithExcerpts()
    {
        var connectionString = await MigrateAsync();
        await using var db = CreateDb(connectionString);
        await _fixture.SetScenarioAsync("probe-fail");
        var modelName = UniqueModel();

        // PATCH path: the catalog row exists, so the fail row correlates to it.
        var userId = await SeedUserAsync(db);
        var modelId = await SeedCatalogAsync(
            db, userId, _fixture.BaseUrl, modelName, result: null, probedAt: null);
        var service = CreateService(db, new OpenAICompatibleAgentEngine(_fixture.Client));

        var result = await service.ProbeAsync(
            ConfigFor(_fixture.BaseUrl, modelName), modelId, CancellationToken.None);

        result.Outcome.Should().Be(ProbeOutcome.Fail);

        var rows = await db.AuditLog.AsNoTracking()
            .Where(a => a.Action == AuditActions.ModelProbeFailed && a.TargetId != null)
            .OrderByDescending(a => a.Id)
            .Take(100)
            .ToListAsync();
        var matches = rows.Where(a => a.TargetId == modelId.ToString()).ToList();
        matches.Should().HaveCount(
            1, "a failed probe emits exactly one ModelProbeFailed row correlated to the model id");
        var row = matches.Single();
        row.TargetType.Should().Be("model_catalog");
        row.ActorUserId.Should().BeNull("a probe failure is a system event (§6)");

        using var metadata = JsonDocument.Parse(row.MetadataJson!);
        metadata.RootElement.GetProperty("base_url").GetString().Should().Be(_fixture.BaseUrl);
        metadata.RootElement.GetProperty("model_name").GetString().Should().Be(modelName);
        metadata.RootElement.GetProperty("reason").GetString().Should().Be("no_tool_calls");
        metadata.RootElement.GetProperty("request_excerpt").GetString()
            .Should().Be(CanonicalRequestExcerpt(modelName));
        metadata.RootElement.GetProperty("response_excerpt").GetString()
            .Should().Be(result.ResponseExcerpt);
        Encoding.UTF8.GetByteCount(metadata.RootElement.GetProperty("response_excerpt").GetString()!)
            .Should().BeLessOrEqualTo(256, "SQ-M3-3: the persisted excerpt is at most 256 UTF-8 bytes");
        metadata.RootElement.GetProperty("duration_ms").GetInt32().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task ProbeAsync_AuditRowMetadata_DoesNotContainApiKey()
    {
        var connectionString = await MigrateAsync();
        await using var db = CreateDb(connectionString);
        await _fixture.SetScenarioAsync("probe-fail");
        var modelName = UniqueModel();
        var service = CreateService(db, new OpenAICompatibleAgentEngine(_fixture.Client));

        // POST path: no correlation id → target_id stays NULL (SQ-M3-11).
        var result = await service.ProbeAsync(ConfigFor(_fixture.BaseUrl, modelName), null, CancellationToken.None);

        result.Outcome.Should().Be(ProbeOutcome.Fail);

        var mine = Mentioning(await ProbeFailRowsAsync(db), modelName);
        mine.Should().HaveCount(1);
        var row = mine.Single();
        row.TargetId.Should().BeNull("the POST path has no catalog row yet");
        var metadataJson = row.MetadataJson!;
        metadataJson.Should().NotContain(
            ApiKey,
            "the fixture body embeds the key marker — a raw-body excerpt or an EngineConfigJson "
            + "serialization must fail this test");
        metadataJson.Should().NotContain("api_key");

        // request_excerpt is a JSON string value (escaped inside the jsonb
        // metadata; Postgres also normalizes the outer formatting) — parse,
        // then compare the decoded body byte-for-byte.
        using var metadata = JsonDocument.Parse(metadataJson);
        metadata.RootElement.GetProperty("request_excerpt").GetString().Should().Be(
            CanonicalRequestExcerpt(modelName),
            "the canonical wire body (key-free — the key rides the Bearer header) is the request_excerpt");
    }

    // -----------------------------------------------------------------------
    // Wire shape (Q-L15 follow-up): the transmitted body carries the
    // canonical max_tokens: 1 — the audit request_excerpt's canonical field
    // set is what actually went over the wire.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ProbeAsync_TransmittedBody_MatchesCanonicalRequestExcerpt()
    {
        var connectionString = await MigrateAsync();
        await using var db = CreateDb(connectionString);
        await _fixture.SetScenarioAsync("probe-pass");
        var modelName = UniqueModel();
        var service = CreateService(db, new OpenAICompatibleAgentEngine(_fixture.Client));

        var result = await service.ProbeAsync(ConfigFor(_fixture.BaseUrl, modelName), null, CancellationToken.None);

        result.Outcome.Should().Be(ProbeOutcome.Pass, "the probe ran (no cache row for a fresh model_name)");

        // The mock captured the raw body of this probe's chat-completions call.
        var wire = _fixture.LastRequestBody;
        wire.Should().NotBeNull("the mock records the body of every request it receives");

        using var transmitted = JsonDocument.Parse(wire!);
        using var excerpt = JsonDocument.Parse(result.RequestExcerpt);

        // Every canonical SQ-M3-3 field the audit excerpt records is exactly
        // what was transmitted — max_tokens: 1 included (Q-L15's cost bound
        // is live, not just audited). The one remaining textual difference
        // between excerpt and wire is M3T1's documented tool `parameters`
        // flattening (M3T2 deviation D7): the engine emits the schema's
        // `properties` object (`{}` for ping), so `parameters` is not compared
        // byte-for-byte here.
        AssertCanonicalProbeShape(transmitted.RootElement, modelName);
        AssertCanonicalProbeShape(excerpt.RootElement, modelName);
    }

    // -----------------------------------------------------------------------
    // Cache window on the shared (base_url, model_name) key.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ProbeAsync_CacheHitWithinWindow_DoesNotCallEngine()
    {
        var connectionString = await MigrateAsync();
        await using var db = CreateDb(connectionString);
        var modelName = UniqueModel();
        var userId = await SeedUserAsync(db);
        await SeedCatalogAsync(
            db, userId, _fixture.BaseUrl, modelName,
            ToolCallingProbeResult.Pass, DateTimeOffset.UtcNow.AddMinutes(-30));
        var service = CreateService(db, new OpenAICompatibleAgentEngine(_fixture.Client));
        var before = TotalRequests();

        var result = await service.ProbeAsync(ConfigFor(_fixture.BaseUrl, modelName), null, CancellationToken.None);

        result.Outcome.Should().Be(ProbeOutcome.PassFromCache, "a within-window Pass row is honored");
        result.CachedFrom.Should().NotBeNull();
        result.RequestExcerpt.Should().BeEmpty("no request ran");
        TotalRequests().Should().Be(before, "the cache hit must not reach the engine");
    }

    [Fact]
    public async Task ProbeAsync_CacheExpired_RunsProbeAgain()
    {
        var connectionString = await MigrateAsync();
        await using var db = CreateDb(connectionString);
        await _fixture.SetScenarioAsync("probe-pass");
        var modelName = UniqueModel();
        var userId = await SeedUserAsync(db);
        await SeedCatalogAsync(
            db, userId, _fixture.BaseUrl, modelName,
            ToolCallingProbeResult.Pass, DateTimeOffset.UtcNow.AddHours(-2));
        var service = CreateService(db, new OpenAICompatibleAgentEngine(_fixture.Client));
        var before = TotalRequests();

        var result = await service.ProbeAsync(ConfigFor(_fixture.BaseUrl, modelName), null, CancellationToken.None);

        result.Outcome.Should().Be(
            ProbeOutcome.Pass, "the 1-hour window lapsed (2 h old row) → the probe re-runs");
        TotalRequests().Should().Be(before + 1);
    }

    [Fact]
    public async Task ProbeAsync_BaseUrlChanged_InvalidatesCache()
    {
        var connectionString = await MigrateAsync();
        await using var db = CreateDb(connectionString);
        await _fixture.SetScenarioAsync("probe-pass");
        var modelName = UniqueModel();
        var userId = await SeedUserAsync(db);
        await SeedCatalogAsync(
            db, userId, _fixture.BaseUrl, modelName,
            ToolCallingProbeResult.Pass, DateTimeOffset.UtcNow.AddMinutes(-30));
        var service = CreateService(db, new OpenAICompatibleAgentEngine(_fixture.Client));
        var before = TotalRequests();

        // Same model_name, different base_url → different cache key. The
        // TestServer client answers any host, so the re-probe still lands on
        // the in-process mock — never a real network call.
        var changedEndpoint = "https://changed.example.com/v1";
        var result = await service.ProbeAsync(
            ConfigFor(changedEndpoint, modelName), null, CancellationToken.None);

        result.Outcome.Should().Be(ProbeOutcome.Pass, "the cache key missed → the probe ran");
        result.CachedFrom.Should().BeNull();
        TotalRequests().Should().Be(before + 1, "a base_url change must trigger a fresh engine call");
    }

    [Fact]
    public async Task ProbeAsync_CacheHit_EmitsNoAuditRow()
    {
        var connectionString = await MigrateAsync();
        await using var db = CreateDb(connectionString);
        var modelName = UniqueModel();
        var userId = await SeedUserAsync(db);
        await SeedCatalogAsync(
            db, userId, _fixture.BaseUrl, modelName,
            ToolCallingProbeResult.Pass, DateTimeOffset.UtcNow.AddMinutes(-30));
        var service = CreateService(db, new OpenAICompatibleAgentEngine(_fixture.Client));

        var result = await service.ProbeAsync(ConfigFor(_fixture.BaseUrl, modelName), null, CancellationToken.None);

        result.Outcome.Should().Be(ProbeOutcome.PassFromCache);
        Mentioning(await ProbeFailRowsAsync(db), modelName).Should().BeEmpty(
            "a cache hit is not a failure — it emits no ModelProbeFailed row");
    }

    // -----------------------------------------------------------------------
    // Helpers.
    // -----------------------------------------------------------------------

    /// <summary>No per-test setup — the fixtures handle it; the lifecycle pair exists for the cleanup below.</summary>
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Per-test cleanup (xUnit instantiates the class once per test): removes
    /// the <c>model_catalog</c> rows this test seeded on the SHARED container
    /// database. The <see cref="MarkerPrefix"/> marker is unique to this
    /// class, so the delete is precise; without it, a class asserting a
    /// zero-row <c>model_catalog</c> downstream (e.g.
    /// <c>SourcesFetchNowTests.M2_NoLlmCall_Happens</c>) fails whenever it
    /// runs after this one. Audit rows are deliberately left in place —
    /// <c>audit_log</c> is append-only (the HMAC chain spans the shared
    /// table, so deletion would break <c>VerifyChainAsync</c> assertions),
    /// and every probe-row assertion in this class correlates by marker.
    /// </summary>
    public async Task DisposeAsync()
    {
        await using var db = CreateDb(_postgres.ConnectionString);
        await db.ModelCatalog
            .Where(m => m.ModelName.StartsWith(MarkerPrefix))
            .ExecuteDeleteAsync();
    }

    private static string UniqueModel() => $"{MarkerPrefix}{Guid.NewGuid():N}";

    private static OpenAICompatibleConfig ConfigFor(string baseUrl, string modelName)
        => new(baseUrl, ApiKey, modelName);

    private static EmailAgentDbContext CreateDb(string connectionString) => new(
        new DbContextOptionsBuilder<EmailAgentDbContext>().UseNpgsql(connectionString).Options);

    private static ModelProbeService CreateService(EmailAgentDbContext db, OpenAICompatibleAgentEngine engine) =>
        new(
            db,
            new AuditLogger(
                db,
                Options.Create(new AuditOptions { HmacKey = AuditHmacKey }),
                NullLogger<AuditLogger>.Instance),
            Options.Create(new ModelOptions()),
            TimeProvider.System,
            () => engine);

    /// <summary>Engine round-trips against this fixture's mock, summed over scenarios (set at test start).</summary>
    private int TotalRequests() => _fixture.RequestCounts.Values.Sum();

    private static async Task<Guid> SeedUserAsync(EmailAgentDbContext db)
    {
        var email = $"m3t2-{Guid.NewGuid():N}@example.test";
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserName = email,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(),
            Role = UserRole.User,
            Status = UserStatus.Active,
            PasswordHash = "not-a-real-hash-m3t2-fixture",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>Seeds a model_catalog row (real users FK) for the cache scenarios.</summary>
    private static async Task<Guid> SeedCatalogAsync(
        EmailAgentDbContext db,
        Guid createdBy,
        string baseUrl,
        string modelName,
        ToolCallingProbeResult? result,
        DateTimeOffset? probedAt)
    {
        var id = Guid.NewGuid();
        db.ModelCatalog.Add(
            new ModelCatalogEntry
            {
                Id = id,
                DisplayName = $"M3T2 {modelName}",
                Provider = "openai-compatible",
                BaseUrl = baseUrl,
                ModelName = modelName,
                Enabled = true,
                CreatedBy = createdBy,
                CreatedAt = DateTimeOffset.UtcNow,
                ToolCallingProbeResult = result,
                ToolCallingProbedAt = probedAt,
            });
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>The newest ModelProbeFailed rows on the shared container database.</summary>
    private static async Task<List<AuditLog>> ProbeFailRowsAsync(EmailAgentDbContext db) =>
        await db.AuditLog.AsNoTracking()
            .Where(a => a.Action == AuditActions.ModelProbeFailed)
            .OrderByDescending(a => a.Id)
            .Take(100)
            .ToListAsync();

    /// <summary>Rows of this test's probe only — matched by the unique model_name marker.</summary>
    private static List<AuditLog> Mentioning(List<AuditLog> rows, string modelName) =>
        rows.Where(r => (r.MetadataJson ?? string.Empty).Contains(modelName, StringComparison.Ordinal))
            .ToList();

    /// <summary>The SQ-M3-3 canonical request_excerpt for a model (byte-for-byte expectation).</summary>
    private static string CanonicalRequestExcerpt(string modelName) =>
        "{\"model\":\"" + modelName + "\","
        + "\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}],"
        + "\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"ping\","
        + "\"description\":\"Probe tool; returns nothing.\","
        + "\"parameters\":{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}}}],"
        + "\"tool_choice\":\"auto\",\"max_tokens\":1,\"stream\":false}";

    /// <summary>
    /// Asserts one probe request body (transmitted or audited) carries the
    /// canonical SQ-M3-3 field set: model, the single user "ping" message, the
    /// single ping tool, <c>tool_choice: "auto"</c>, <c>max_tokens: 1</c> and
    /// <c>stream: false</c> (Q-L15 — the wire now carries the cost bound).
    /// </summary>
    private static void AssertCanonicalProbeShape(JsonElement root, string modelName)
    {
        root.GetProperty("model").GetString().Should().Be(modelName);
        root.GetProperty("max_tokens").GetInt32().Should().Be(1, "SQ-M3-3 canonical cost bound (Q-L15)");
        root.GetProperty("stream").GetBoolean().Should().BeFalse();
        root.GetProperty("tool_choice").GetString().Should().Be("auto");

        JsonElement message = root.GetProperty("messages")[0];
        message.GetProperty("role").GetString().Should().Be("user");
        message.GetProperty("content").GetString().Should().Be("ping");

        JsonElement function = root.GetProperty("tools")[0].GetProperty("function");
        function.GetProperty("name").GetString().Should().Be("ping");
        function.GetProperty("description").GetString().Should().Be("Probe tool; returns nothing.");
    }

    /// <summary>
    /// The probe-fail excerpt the reconstruction must produce from the fixture
    /// (content + empty tool_calls + usage 4/9) — serialized with the same
    /// shared options, so the assertion is byte-exact for any encoder policy.
    /// </summary>
    private static string ExpectedFailResponse() => JsonSerializer.Serialize(
        new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content = "I can't do tool calls",
                        tool_calls = Array.Empty<object>(),
                    },
                },
            },
            usage = new { prompt_tokens = 4, completion_tokens = 9 },
        },
        OpenAIJsonOptions.Default);

    /// <summary>Applies every migration once per shared Postgres container (InitialSchemaTests pattern).</summary>
    private async Task<string> MigrateAsync()
    {
        var connectionString = _postgres.ConnectionString;

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
