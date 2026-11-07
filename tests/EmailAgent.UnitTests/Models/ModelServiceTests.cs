using System.Text;
using System.Text.Json;
using EmailAgent.Api.Endpoints;
using EmailAgent.Api.Models;
using EmailAgent.Api.Pagination;
using EmailAgent.Api.Sources;
using EmailAgent.Connectors.OpenAI;
using EmailAgent.Core.Audit;
using EmailAgent.Core.Security;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using EmailAgent.Infrastructure.Security;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EmailAgent.UnitTests.Models;

// ---------------------------------------------------------------------------
// M3T3: ModelService against EF InMemory + a fake IModelProbeService and a
// recording audit sink — so the probe-before-write gate (SQ-M3-11), the
// M3T1 schema gate ordering, the keyset list (SQ-M3-2) and the key-free
// audit/DTO projections are all provable without HTTP or a container.
// ---------------------------------------------------------------------------

[Trait("Category", "M3T3")]
public class ModelServiceTests
{
    private const string BaseUrl = "https://unit.example.com/v1";
    private const string Provider = "openai-compatible";
    private const string ApiKey = "sk-m3t3-unit-service-key";

    // -----------------------------------------------------------------------
    // Task-named test: filters + (created_at DESC, id DESC) keyset cursor.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ModelService_List_AppliesFilters_AndCursor()
    {
        var db = CreateDb();
        var service = CreateService(db, new FakeProbe(Pass()), new RecordingAudit());

        var now = DateTimeOffset.UtcNow;
        var alpha = Seed(db, "m3t3-unit-alpha", Provider, true, now.AddMinutes(-1));
        var beta = Seed(db, "m3t3-unit-beta", Provider, true, now.AddMinutes(-2));
        var disabled = Seed(db, "m3t3-unit-disabled", Provider, false, now.AddMinutes(-3));
        var acme = Seed(db, "m3t3-unit-acme", "acme-llm", true, now.AddMinutes(-4));
        var tieTime = now.AddMinutes(-5);
        var tieA = Seed(db, "m3t3-unit-tie-a", Provider, true, tieTime);
        var tieB = Seed(db, "m3t3-unit-tie-b", Provider, true, tieTime);
        var tieFirst = tieA.CompareTo(tieB) > 0 ? tieA : tieB;
        var tieSecond = tieFirst == tieA ? tieB : tieA;

        // filter[enabled]
        var enabledPage = await service.ListAsync(true, null, null, null, 50, CancellationToken.None);
        enabledPage.Items.Select(item => item.Id).Should().Equal(
            new[] { alpha, beta, acme, tieFirst, tieSecond },
            "enabled=false rows are excluded; order is (created_at DESC, id DESC) — the tie pair breaks by id DESC");

        // filter[provider]
        var providerPage = await service.ListAsync(null, "acme-llm", null, null, 50, CancellationToken.None);
        providerPage.Items.Select(item => item.Id).Should().Equal(acme);

        // filter[display_name_contains]
        var namePage = await service.ListAsync(null, null, "beta", null, 50, CancellationToken.None);
        namePage.Items.Select(item => item.Id).Should().Equal(beta);

        // Combined filters compose.
        var combined = await service.ListAsync(
            true, Provider, "m3t3-unit", null, 50, CancellationToken.None);
        combined.Items.Select(item => item.Id).Should().Equal(
            new[] { alpha, beta, tieFirst, tieSecond }, "both filters apply at once");

        // Keyset walk over the unfiltered catalog: limit 2 → next_cursor on
        // every non-final page, omitted on the last one (§13.13).
        var pageOne = await service.ListAsync(null, null, null, null, 2, CancellationToken.None);
        pageOne.Items.Select(item => item.Id).Should().Equal(alpha, beta);
        pageOne.NextCursor.Should().NotBeNullOrWhiteSpace("a further page exists");

        var pageTwo = await service.ListAsync(
            null, null, null, pageOne.NextCursor, 2, CancellationToken.None);
        pageTwo.Items.Select(item => item.Id).Should().Equal(disabled, acme);
        pageTwo.NextCursor.Should().NotBeNullOrWhiteSpace("the tie pair is still ahead");

        var pageThree = await service.ListAsync(
            null, null, null, pageTwo.NextCursor, 2, CancellationToken.None);
        pageThree.Items.Select(item => item.Id).Should().Equal(tieFirst, tieSecond);
        pageThree.NextCursor.Should().BeNull("the last page omits next_cursor");

        // Out-of-range page sizes are rejected before any query runs.
        var limitAct = () => service.ListAsync(
            null, null, null, null, PaginationQuery.MinLimit - 1, CancellationToken.None);
        await limitAct.Should().ThrowAsync<ArgumentOutOfRangeException>();

        // A crafted cursor is a typed §13.12 400 at the endpoint, never a 500
        // (review S2: the decode failure must stay an InvalidCursorException).
        var badCursorAct = () => service.ListAsync(
            null, null, null, "%%%not-a-cursor%%%", PaginationQuery.DefaultLimit,
            CancellationToken.None);
        await badCursorAct.Should().ThrowAsync<InvalidCursorException>(
            "a malformed cursor must decode to a 400 /errors/validation, never an unhandled error");
    }

    // -----------------------------------------------------------------------
    // Gate ordering: schema → probe → transaction/writes.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ModelService_Create_RejectsConfigValidationFailure_WithProbeNotInvoked()
    {
        var db = CreateDb();
        var probe = new FakeProbe(Pass());
        var audit = new RecordingAudit();
        var service = CreateService(db, probe, audit);

        // ftp:// violates the schema's ^https?:// pattern on base_url.
        var request = ValidRequest(baseUrl: "ftp://example.test/v1");

        var act = () => service.CreateAsync(request, Actor(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ConfigValidationException>(
            "the M3T1 engine schema gates before anything else (task step 3)");
        exception.Which.Issues.Should().Contain(
            issue => issue.Path.Contains("base_url", StringComparison.Ordinal),
            "the failing member must be blamed by name in errors[]");
        probe.Calls.Should().Be(0, "no socket may open for a schema-invalid config");
        audit.Events.Should().BeEmpty("nothing was attempted, so nothing is audited");
        db.SaveChangesCount.Should().Be(0, "no write of any kind ran");
        db.ModelCatalog.Should().BeEmpty();
        db.EncryptedBlobs.Should().BeEmpty();
    }

    [Fact]
    public async Task ModelService_Create_RejectsProbeFailure_WithNoRowWritten()
    {
        var db = CreateDb();
        var probe = new FakeProbe(
            ProbeResult.FailResult(
                "no_tool_calls",
                "{\"model\":\"m3t3-unit\"}",
                "{\"choices\":[],\"usage\":{\"prompt_tokens\":4,\"completion_tokens\":9}}",
                DateTimeOffset.UtcNow));
        var audit = new RecordingAudit();
        var service = CreateService(db, probe, audit);

        var act = () => service.CreateAsync(ValidRequest(), Actor(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ModelProbeFailedException>(
            "a failing probe is a non-recoverable 400 (SQ-M3-11)");
        exception.Which.Reason.Should().Be("no_tool_calls");
        exception.Which.ResponseExcerpt.Should().Contain("completion_tokens");
        probe.Calls.Should().Be(1, "the probe ran exactly once, BEFORE any write");
        db.SaveChangesCount.Should().Be(
            0, "SQ-M3-11: the probe's own fail audit row is the only write — the service saves nothing");
        db.ModelCatalog.Should().BeEmpty("no model_catalog row may exist after a failed probe");
        db.EncryptedBlobs.Should().BeEmpty("no encrypted_blobs row may exist after a failed probe");
        audit.Events.Should().BeEmpty(
            "the fail-only ModelProbeFailed row belongs to the probe service, not to ModelService");
    }

    [Fact]
    public async Task ModelService_Create_ProbePass_WritesRowAndSystemKeyBlob_WithoutKeyInAudit()
    {
        var db = CreateDb();
        var probe = new FakeProbe(Pass());
        var audit = new RecordingAudit();
        var service = CreateService(db, probe, audit);
        var actor = Actor();

        var dto = await service.CreateAsync(ValidRequest(), actor, CancellationToken.None);

        // Row + system-keyed blob, two SaveChanges inside one transaction.
        db.SaveChangesCount.Should().Be(
            2, "one save for the key blob and one for the catalog row");
        var row = db.ModelCatalog.Should().ContainSingle().Which;
        row.CreatedBy.Should().Be(actor.UserId);
        row.Enabled.Should().BeTrue("enabled defaults to true (§6)");
        row.ToolCallingProbeResult.Should().Be(ToolCallingProbeResult.Pass);
        row.ToolCallingProbedAt.Should().Be(probe.Result.ProbedAt, "the probe's timestamp stamps the row");

        var blobRef = Guid.Parse(row.ApiKeySecretRef!);
        var blob = await db.EncryptedBlobs.AsNoTracking().SingleAsync(b => b.Id == blobRef);
        blob.Purpose.Should().Be(EncryptedModelApiKeyStore.ModelApiKeyPurpose);
        blob.OwnerUserId.Should().BeNull();
        Encoding.UTF8.GetString(blob.Ciphertext).Should().NotContain(
            ApiKey, "the key is wrapped under the system key, never stored in clear");

        // Q-O17 audit row: probe metadata, never credential material.
        var evt = audit.Events.Should()
            .ContainSingle(e => e.Action == AuditActions.CreateModel).Which;
        evt.TargetType.Should().Be("model_catalog");
        evt.TargetId.Should().Be(row.Id.ToString());
        evt.ActorUserId.Should().Be(actor.UserId);
        evt.ActorRole.Should().Be(actor.Role);
        evt.MetadataJson.Should().NotContain(ApiKey, "no key material in metadata_json");
        evt.MetadataJson.Should().NotContain("api_key");

        using var metadata = JsonDocument.Parse(evt.MetadataJson!);
        metadata.RootElement.GetProperty("model_id").GetString().Should().Be(row.Id.ToString());
        metadata.RootElement.GetProperty("probe_outcome").GetString().Should().Be("Pass");
        metadata.RootElement.GetProperty("probed_at").ValueKind.Should().NotBe(JsonValueKind.Null);
        metadata.RootElement.GetProperty("display_name").GetString().Should().Be(row.DisplayName);

        // The projection is a fixed member list with no credential member.
        typeof(ModelDto).GetProperty("ApiKey").Should().BeNull(
            "the published ModelDto has no api_key property (task projection)");
        typeof(ModelDto).GetProperty("ApiKeySecretRef").Should().BeNull();
        dto.Id.Should().Be(row.Id);
        dto.ToolCallingProbeResult.Should().Be("Pass");
        dto.CreatedBy.Should().Be(actor.UserId);
    }

    // -----------------------------------------------------------------------
    // Stubs.
    // -----------------------------------------------------------------------

    private static ProbeResult Pass()
        => ProbeResult.PassResult("{}", "{}", DateTimeOffset.UtcNow);

    private static CreateModelRequest ValidRequest(string baseUrl = BaseUrl)
        => new()
        {
            DisplayName = "M3T3 unit model",
            Provider = Provider,
            BaseUrl = baseUrl,
            ModelName = "m3t3-unit-model",
            ApiKey = ApiKey,
            PricePer1kInputUsd = 0.0015m,
            PricePer1kOutputUsd = 0.002m,
        };

    private static ModelActor Actor() => new(Guid.NewGuid(), "Admin", null);

    private static ModelService CreateService(
        CountingDbContext db, FakeProbe probe, RecordingAudit audit) =>
        new(
            db,
            probe,
            new EncryptedModelApiKeyStore(db, CreateEncryption(), TimeProvider.System),
            audit,
            TimeProvider.System);

    private static Guid Seed(
        CountingDbContext db,
        string displayName,
        string provider,
        bool enabled,
        DateTimeOffset createdAt)
    {
        var id = Guid.NewGuid();
        db.ModelCatalog.Add(
            new ModelCatalogEntry
            {
                Id = id,
                DisplayName = displayName,
                Provider = provider,
                BaseUrl = BaseUrl,
                ModelName = displayName,
                Enabled = enabled,
                CreatedBy = Guid.NewGuid(),
                CreatedAt = createdAt,
            });
        db.SaveChanges();
        return id;
    }

    /// <summary>
    /// InMemory seam (probe-suite pattern): drop the jsonb-mapped
    /// <see cref="User.KdfParams"/> property EF InMemory cannot map, ignore
    /// the transaction-ignored warning (M3T3 opens one transaction per
    /// create/update/delete), and count every save so the probe-before-write
    /// gate can be asserted precisely.
    /// </summary>
    private sealed class CountingDbContext(DbContextOptions<EmailAgentDbContext> options)
        : EmailAgentDbContext(options)
    {
        public int SaveChangesCount { get; private set; }

        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            SaveChangesCount++;
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        public override Task<int> SaveChangesAsync(
            bool acceptAllChangesOnSuccess,
            CancellationToken cancellationToken = default)
        {
            SaveChangesCount++;
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<User>().Ignore(u => u.KdfParams);
        }
    }

    /// <summary>The M3T2 probe seam: canned result, call counter, no HTTP.</summary>
    private sealed class FakeProbe(ProbeResult result) : IModelProbeService
    {
        public ProbeResult Result => result;

        public int Calls { get; private set; }

        public Task<ProbeResult> ProbeAsync(
            OpenAICompatibleConfig config, Guid? correlationModelId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    /// <summary>M1T4 audit sink that records rows in memory (probe-suite pattern).</summary>
    private sealed class RecordingAudit : IAuditLogger
    {
        public List<AuditEvent> Events { get; } = new();

        public Task LogAsync(AuditEvent evt, CancellationToken ct)
        {
            Events.Add(evt);
            return Task.CompletedTask;
        }

        public Task<long?> VerifyChainAsync(CancellationToken ct) => Task.FromResult<long?>(null);
    }

    private static CountingDbContext CreateDb() => new(
        new DbContextOptionsBuilder<EmailAgentDbContext>()
            .UseInMemoryDatabase($"m3t3-service-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static IEncryptionService CreateEncryption()
        => new Argon2idAesGcmEncryptionService(
            Options.Create(
                new EncryptionOptions
                {
                    KeyEncryptionKey = "m3t3-unit-service-kek-0123456789abcdef",
                }),
            NullLogger<Argon2idAesGcmEncryptionService>.Instance);
}
