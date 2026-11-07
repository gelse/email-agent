using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmailAgent.Core.Audit;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Audit;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.IntegrationTests.Endpoints;
using EmailAgent.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace EmailAgent.IntegrationTests.M3EndToEnd;

// ---------------------------------------------------------------------------
// M3T7 step 5: the in-process equivalent of M1T9's tools/AuditVerify. The fact
// first PRODUCES all five new M3 actions through the real endpoints (create →
// PATCH rename → probe-fail create → test-completion → delete), then walks the
// whole audit_log chain exactly the way tools/AuditVerify does: genesis anchor,
// rows in id order, HMAC-SHA256 over prevHmac || canonical row bytes. Two
// independent checks run over the same table:
//
//   1. a manual AuditHmacChain.ComputeGenesis/ComputeRow walk written HERE
//      (so the test does not lean on the production verifier alone), and
//   2. IAuditLogger.VerifyChainAsync — the in-process twin AuditVerify mirrors.
//
// No socket, no real provider: the probe and the completion both ride the
// in-process OpenAIMock (AGENTS.md test constraints).
// ---------------------------------------------------------------------------

[Trait("Category", "M3EndToEnd")]
[Collection(M3E2ECollection.Name)]
public sealed class AuditChainTests : IClassFixture<OpenAIMockFixture>, IAsyncLifetime
{
    /// <summary>The five M3 audit actions that must all appear in the chain (task acceptance).</summary>
    private static readonly string[] M3Actions =
    [
        AuditActions.CreateModel,
        AuditActions.UpdateModel,
        AuditActions.DeleteModel,
        AuditActions.ModelProbeFailed,
        AuditActions.TestModelCompletion,
    ];

    private readonly PostgresFixture _postgres;
    private readonly OpenAIMockFixture _mock;
    private readonly List<string> _markers = new();

    public AuditChainTests(PostgresFixture postgres, OpenAIMockFixture mock)
    {
        _postgres = postgres;
        _mock = mock;
    }

    /// <summary>
    /// <c>AuditChain_AfterM3Flow_StillValid</c>: every audit row written by the
    /// full M3 flow reproduces its stored HMAC from the canonical chain
    /// helper, the ids are contiguous (append-only), the production verifier
    /// agrees, and all five new actions are present in the chain.
    /// </summary>
    [Fact]
    public async Task AuditChain_AfterM3Flow_StillValid()
    {
        var marker = M3TestHarness.NewMarker();
        _markers.Add(marker);
        var host = await M3TestHarness.CreateHostAsync(_postgres, marker, _mock, "probe-pass");
        await using var factory = host.Factory;

        // ------------------------------------------------------------------
        // Produce the five actions through the real endpoints, in an order
        // that does not depend on any other test in this collection.
        // ------------------------------------------------------------------

        // (1) create_model — a passing probe writes row + key blob + audit row.
        var created = await M3TestHarness.CreateModelAsync(
            host,
            M3TestHarness.CreateBody(host.Marker, _mock.BaseUrl, $"{marker} chain model"));
        var modelId = created.GetProperty("id").GetGuid();

        // (2) update_model — a within-window display_name rename is served
        //     PassFromCache (no HTTP) and still writes its audit row.
        using (var patch = await M3TestHarness.SendAsync(
                   host,
                   HttpMethod.Patch,
                   $"/v1/admin/models/{modelId}",
                   new
                   {
                       display_name = $"{marker} renamed",
                       base_url = (string?)null,
                       api_key = (string?)null,
                   }))
        {
            patch.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // (3) model_probe_failed — a failing probe writes NO row and NO blob,
        //     only the fail-only audit row with target_id IS NULL.
        await _mock.SetScenarioAsync("probe-fail");
        using (var failed = await M3TestHarness.SendAsync(
                   host,
                   HttpMethod.Post,
                   "/v1/admin/models",
                   M3TestHarness.CreateBody(
                       host.Marker, _mock.BaseUrl, $"{marker} doomed", $"{marker}-doomed")))
        {
            var problem = await AccountTestHost.ReadProblemAsync(failed, HttpStatusCode.BadRequest);
            problem.GetProperty("type").GetString().Should().Be("/errors/model-probe-failed");
        }

        // (4) test_model_completion — the manual tool round-trips the prompt.
        await _mock.SetScenarioAsync("happy");
        using (var completion = await M3TestHarness.SendAsync(
                   host,
                   HttpMethod.Post,
                   $"/v1/admin/models/{modelId}/test-completion",
                   new
                   {
                       system_prompt = M3TestHarness.SystemPrompt,
                       user_prompt = M3TestHarness.UserPrompt,
                   }))
        {
            completion.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // (5) delete_model — 204, row + blob gone, audit row kept.
        using (var deleted = await M3TestHarness.SendAsync(
                   host, HttpMethod.Delete, $"/v1/admin/models/{modelId}"))
        {
            deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        // ------------------------------------------------------------------
        // Check A: the manual walk tools/AuditVerify performs, recomputed here.
        // ------------------------------------------------------------------
        var key = DeriveAuditKey(factory);
        var rows = await ReadAuditAsync(factory);
        rows.Should().NotBeEmpty("the M3 flow above appended audit rows");

        var prev = AuditHmacChain.ComputeGenesis(key);
        var lastId = 0L;
        foreach (var row in rows)
        {
            row.Id.Should().Be(
                lastId + 1, $"audit ids are contiguous — row {row.Id} shows a gap or a deletion");
            row.Hmac.Should().NotBeNull("every row carries its chain HMAC");
            row.Hmac!.Length.Should().Be(32, "HMAC-SHA256 — what smoke-m3.sh asserts as octet_length");

            var payload = ToPayload(row);
            var expected = AuditHmacChain.ComputeRow(key, prev, payload);
            expected.AsSpan().SequenceEqual(row.Hmac).Should().BeTrue(
                $"row {row.Id} ({row.Action}) must reproduce from prevHmac || canonical bytes — "
                    + "this is the exact recompute tools/AuditVerify does");

            prev = row.Hmac;
            lastId = row.Id;
        }

        // ------------------------------------------------------------------
        // Check B: the in-process twin of AuditVerify's walk (M1T9 parity) —
        // it returns the first bad row id, null = chain valid.
        // ------------------------------------------------------------------
        using (var scope = factory.Services.CreateScope())
        {
            var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
            (await audit.VerifyChainAsync(CancellationToken.None)).Should().BeNull(
                "IAuditLogger.VerifyChainAsync is the in-process equivalent of tools/AuditVerify");
        }

        // ------------------------------------------------------------------
        // Check C: the five NEW M3 actions are all present in the chain.
        // ------------------------------------------------------------------
        foreach (var action in M3Actions)
        {
            rows.Where(row => row.Action == action).Should().NotBeEmpty(
                $"the chain must contain at least one '{action}' row (task acceptance criterion)");
        }

        rows.Where(row => row.TargetId == modelId.ToString())
            .Select(row => row.Action)
            .Should().Contain(new[]
            {
                AuditActions.CreateModel,
                AuditActions.UpdateModel,
                AuditActions.TestModelCompletion,
                AuditActions.DeleteModel,
            }, "the four model-scoped actions all target this model id");

        // The probe-fail row correlates to THIS flow by its unique marker,
        // inside the filter itself: the M3EndToEnd collection runs
        // sequentially and its other flows also append model_probe_failed
        // rows, so filtering on the whole action would make ContainSingle
        // depend on test order.
        var probeFailed = rows.Where(
                row => row.Action == AuditActions.ModelProbeFailed
                    && (row.MetadataJson ?? string.Empty).Contains(
                        marker, StringComparison.Ordinal))
            .ToList();
        probeFailed.Should().ContainSingle(
            "exactly one probe-fail row correlates to this fact's marker");
        probeFailed[0].TargetId.Should().BeNull(
            "a rejected create has no catalog row yet (SQ-M3-11)");
        (probeFailed[0].MetadataJson ?? string.Empty).Should().NotContain(
            M3TestHarness.ApiKey, "no key material ever reaches metadata_json");

        // Every M3 row keeps the shape the smoke script's SQL asserts.
        rows.Where(row => M3Actions.Contains(row.Action))
            .Should().OnlyContain(row => row.TargetType == "model_catalog"
                                          && row.Hmac != null
                                          && row.Hmac.Length == 32);
    }

    // -----------------------------------------------------------------------
    // Lifecycle (one class instance per fact) + per-fact cleanup
    // -----------------------------------------------------------------------

    /// <summary>No per-test setup — the flow above is the setup seam.</summary>
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Marker-keyed cleanup (blobs first, then rows). Audit rows are NEVER
    /// deleted — the chain spans them, and this fact asserts exactly that.
    /// </summary>
    public async Task DisposeAsync()
    {
        foreach (var marker in _markers)
        {
            await M3TestHarness.CleanupAsync(_postgres, marker);
        }
    }

    // -----------------------------------------------------------------------
    // Chain-verification helpers (the tools/AuditVerify recompute, in process)
    // -----------------------------------------------------------------------

    /// <summary>All audit rows in chain order (id ASC) — the verifier's read.</summary>
    private static async Task<List<AuditLog>> ReadAuditAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return await db.AuditLog.AsNoTracking()
            .OrderBy(row => row.Id)
            .ToListAsync();
    }

    /// <summary>
    /// The per-install key exactly as <see cref="AuditLogger"/> derives it:
    /// UTF-8 bytes when the configured literal is already 32 bytes, otherwise
    /// hashed down — never lossy truncation (Q-O10 / SQ-2).
    /// </summary>
    private static byte[] DeriveAuditKey(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var configured = scope.ServiceProvider
            .GetRequiredService<IOptions<AuditOptions>>()
            .Value.HmacKey;
        configured.Should().NotBeNullOrWhiteSpace(
            "Audit:HmacKey is ValidateOnStart — every factory-hosted host has one");

        var raw = Encoding.UTF8.GetBytes(configured!);
        return raw.Length == 32 ? raw : SHA256.HashData(raw);
    }

    /// <summary>
    /// The nine chained columns exactly as the writer serializes them — the
    /// stored (DB-sourced) values, because jsonb may normalize metadata key
    /// order and timestamptz rounds sub-microsecond ticks (AuditLogger's rule:
    /// write-time bytes must equal verify-time bytes).
    /// </summary>
    private static AuditRowPayload ToPayload(AuditLog row) => new()
    {
        Id = row.Id,
        ActorUserId = row.ActorUserId,
        ActorRole = row.ActorRole,
        Action = row.Action,
        TargetType = row.TargetType,
        TargetId = row.TargetId,
        MetadataJson = row.MetadataJson,
        Ip = row.Ip?.ToString(),
        CreatedAt = row.CreatedAt.ToUnixTimeSeconds(),
    };
}
