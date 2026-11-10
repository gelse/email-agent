using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using EmailAgent.Api.Endpoints;
using EmailAgent.Api.Models;
using EmailAgent.Api.Pagination;
using EmailAgent.Connectors.OpenAI;
using EmailAgent.Connectors.OpenAI.Http;
using EmailAgent.Core.Audit;
using EmailAgent.Contracts.AgentEngines;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Audit;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using EmailAgent.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace EmailAgent.IntegrationTests.Endpoints;

// ---------------------------------------------------------------------------
// M3T3: the five /v1/admin/models endpoints (task's 15 pinned integration
// tests) against Testcontainers Postgres + the real host, with the probe
// driving the in-process OpenAIMock — no socket, no real provider (AGENTS.md
// test constraint).
//
// Isolation rules (the shared container database is used by every Postgres
// collection test):
//  - one unique `m3t3-<guid>` marker per test: it is the display-name filter
//    the list tests isolate on AND the model_name prefix DisposeAsync keys
//    its cleanup on;
//  - audit rows are NEVER deleted (audit_log is append-only and the HMAC
//    chain spans the shared table) — every audit assertion correlates by
//    marker or model id;
//  - one OpenAIMockFixture per class: SetScenarioAsync switches the active
//    scenario per test (recorded deviation vs the task's "second fixture"
//    parenthetical — the TestServer client answers ANY host, so a patched
//    base_url still lands on this fixture's active scenario).
// ---------------------------------------------------------------------------

[Trait("Category", "M3T3")]
[Collection("Postgres")]
public class AdminModelEndpointsTests(OpenAIMockFixture fixture, PostgresFixture postgres)
    : IClassFixture<OpenAIMockFixture>, IAsyncLifetime
{
    /// <summary>Throwaway integration-test api_key (never a real credential).</summary>
    private const string ApiKey = "sk-test-redacted-m3t3";

    private const string Provider = "openai-compatible";

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

    private string? _marker;

    /// <summary>
    /// Review S3 seam: while set to an audit action, the host's audit sink
    /// fails that action — inside the open create/update/delete transaction —
    /// so the rollback of row + key blob can be proven, not just the happy
    /// end state. Read per call by <see cref="SabotageAuditLogger"/>.
    /// </summary>
    private string? _sabotageAction;

    // -----------------------------------------------------------------------
    // POST /v1/admin/models
    // -----------------------------------------------------------------------

    /// <summary>Task row: a passing probe encrypts the key, writes row + blob, and returns no key.</summary>
    [Fact]
    public async Task Create_HappyPath_RunsProbeEncryptsKeyAndReturnsWithoutKey()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;
        var requestsBefore = TotalRequests();

        using var response = await client.SendAsync(
            AdminTestHost.Request(HttpMethod.Post, "/v1/admin/models", host.Bearer, CreateBody(host.Marker)));

        response.StatusCode.Should().Be(HttpStatusCode.Created, "a passing probe yields 201 (task scope table)");
        TotalRequests().Should().Be(requestsBefore + 1, "the M3T2 probe ran against the in-process mock");
        var modelId = Guid.Empty;
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain(ApiKey, "the API key must never reach a response body");
        using (var document = JsonDocument.Parse(raw))
        {
            JsonElement dto = document.RootElement.Clone();
            AssertDtoShape(dto);
            modelId = dto.GetProperty("id").GetGuid();
            dto.GetProperty("display_name").GetString().Should().Be($"{host.Marker} model");
            dto.GetProperty("provider").GetString().Should().Be(Provider);
            dto.GetProperty("base_url").GetString().Should().Be(fixture.BaseUrl);
            dto.GetProperty("model_name").GetString().Should().Be(host.Marker);
            dto.GetProperty("enabled").GetBoolean().Should().BeTrue();
            dto.GetProperty("tool_calling_probe_result").GetString().Should().Be("Pass");
            dto.GetProperty("tool_calling_probed_at").ValueKind.Should().NotBe(JsonValueKind.Null);
            dto.GetProperty("created_by").GetGuid().Should().Be(host.AdminId);
            dto.GetProperty("price_per_1k_input_usd").GetDecimal().Should().Be(0.0015m);
            dto.GetProperty("price_per_1k_output_usd").GetDecimal().Should().Be(0.002m);
        }

        response.Headers.Location.Should().NotBeNull("201 carries a Location header");
        response.Headers.Location!.ToString().Should().Be($"/v1/admin/models/{modelId}");

        // model_catalog row + system-owned key blob.
        var row = (await FindRowAsync(factory, modelId))!;
        row.Should().NotBeNull("the 201 must be backed by a catalog row");
        row.CreatedBy.Should().Be(host.AdminId);
        row.ToolCallingProbeResult.Should().Be(ToolCallingProbeResult.Pass);
        row.ApiKeySecretRef.Should().NotBeNull();
        var blob = (await FindBlobAsync(factory, Guid.Parse(row.ApiKeySecretRef!)))!;
        blob.Should().NotBeNull("api_key_secret_ref must resolve to an encrypted_blobs row");
        blob.Purpose.Should().Be(
            EncryptedModelApiKeyStore.ModelApiKeyPurpose, "Q-E7: purpose='model_api_key'");
        blob.OwnerUserId.Should().BeNull("Q-E7: owner_user_id IS NULL — system-owned");
        ContainsBytes(blob.Ciphertext, Encoding.UTF8.GetBytes(ApiKey)).Should().BeFalse(
            "ciphertext must not contain the raw api_key bytes (task acceptance criterion)");

        // CreateModel audit row (Q-O17): probe metadata, never key material.
        var auditRow = await SingleAuditAsync(factory, AuditActions.CreateModel, row.Id);
        auditRow.Hmac.Length.Should().Be(32, "audit rows are HMAC-chained (Q-O2)");
        auditRow.TargetType.Should().Be("model_catalog");
        auditRow.ActorUserId.Should().Be(host.AdminId);
        auditRow.ActorRole.Should().Be("Admin");
        auditRow.MetadataJson.Should().NotContain(ApiKey, "no key material in metadata_json");
        auditRow.MetadataJson.Should().NotContain("api_key");
        using var metadata = JsonDocument.Parse(auditRow.MetadataJson!);
        metadata.RootElement.GetProperty("model_id").GetGuid().Should().Be(modelId);
        metadata.RootElement.GetProperty("display_name").GetString().Should().Be($"{host.Marker} model");
        metadata.RootElement.GetProperty("provider").GetString().Should().Be(Provider);
        metadata.RootElement.GetProperty("probe_outcome").GetString().Should().Be("Pass");
        metadata.RootElement.GetProperty("probed_at").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    /// <summary>Task row: a failed probe is 400 /errors/model-probe-failed with the excerpt and NO rows.</summary>
    [Fact]
    public async Task Create_ProbeFails_Returns400ProblemDetails_WithProbeExcerpt()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;
        var marker = host.Marker;
        await fixture.SetScenarioAsync("probe-fail");
        var requestsBefore = TotalRequests();
        var keyBlobsBefore = await CountModelKeyBlobsAsync(factory);

        using var response = await client.SendAsync(
            AdminTestHost.Request(HttpMethod.Post, "/v1/admin/models", host.Bearer, CreateBody(marker)));

        var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.BadRequest);
        problem.GetProperty("type").GetString().Should().Be(
            "/errors/model-probe-failed", "the probe gate's §13.12 error class");
        TotalRequests().Should().Be(requestsBefore + 1, "the probe ran BEFORE any write (SQ-M3-11)");

        var detail = problem.GetProperty("detail").GetString();
        detail.Should().NotBeNullOrWhiteSpace("detail carries the probe's response excerpt");
        Encoding.UTF8.GetByteCount(detail!)
            .Should().BeLessOrEqualTo(256, "SQ-M3-3 clamps the excerpt to 256 UTF-8 bytes");
        detail.Should().Be(
            Utf8Excerpt.Truncate(ExpectedFailResponse(), 256),
            "detail IS the reconstructed response body, clamped byte-for-byte");
        detail.Should().NotContain(
            ApiKey, "task Risks: the excerpt must never echo the api_key VALUE");

        // SQ-M3-11: no catalog row, no key blob — the probe's fail audit row
        // (target_id NULL) is the only write.
        (await RowsNamedAsync(factory, marker)).Should().BeEmpty(
            "a failed probe must not write a model_catalog row");
        (await CountModelKeyBlobsAsync(factory)).Should().Be(
            keyBlobsBefore, "a failed probe must not write an encrypted_blobs row");

        var failRows = await RecentAuditAsync(factory, AuditActions.ModelProbeFailed);
        var mine = failRows
            .Where(row => (row.MetadataJson ?? string.Empty).Contains(marker, StringComparison.Ordinal))
            .ToList();
        mine.Should().ContainSingle("exactly one probe fail row correlates to this request");
        mine[0].TargetId.Should().BeNull("the POST path has no catalog row yet (SQ-M3-11)");
        mine[0].MetadataJson.Should().NotContain(ApiKey);

        var createRows = await RecentAuditAsync(factory, AuditActions.CreateModel);
        createRows
            .Where(row => (row.MetadataJson ?? string.Empty).Contains(marker, StringComparison.Ordinal))
            .Should().BeEmpty("no CreateModel row may exist for the rejected create");
    }

    /// <summary>Task row: a within-window Pass row serves the probe from cache (no HTTP) and the write still happens.</summary>
    [Fact]
    public async Task Create_CacheHitWithinWindow_StillPasses()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;
        var marker = host.Marker;

        // Pre-seeded catalog row: same (base_url, model_name), Pass, 30 min old.
        await SeedRowAsync(
            host, $"{marker} seeded", probeResult: ToolCallingProbeResult.Pass,
            probedAt: DateTimeOffset.UtcNow.AddMinutes(-30));
        var requestsBefore = TotalRequests();

        var dto = await CreateModelAsync(host, CreateBody(marker));

        TotalRequests().Should().Be(
            requestsBefore, "a within-window Pass row serves the probe with no HTTP call (SQ-M3-13)");
        dto.GetProperty("tool_calling_probe_result").GetString().Should().Be("Pass");
        dto.GetProperty("tool_calling_probed_at").ValueKind.Should().NotBe(JsonValueKind.Null);

        (await RowsNamedAsync(factory, marker)).Should().HaveCount(
            2, "the seeded cache row and the newly created row both carry this model_name");

        var auditRow = await SingleAuditAsync(factory, AuditActions.CreateModel, dto.GetProperty("id").GetGuid());
        using var metadata = JsonDocument.Parse(auditRow.MetadataJson!);
        metadata.RootElement.GetProperty("probe_outcome").GetString().Should().Be(
            "PassFromCache", "the audit records the outcome the probe reported");
    }

    // -----------------------------------------------------------------------
    // GET /v1/admin/models/{id}
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Get_ExistingModel_ReturnsDto_NoApiKey()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;
        var created = await CreateModelAsync(host, CreateBody(host.Marker));
        var modelId = created.GetProperty("id").GetGuid();

        using var response = await client.SendAsync(
            AdminTestHost.Request(HttpMethod.Get, $"/v1/admin/models/{modelId}", host.Bearer));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain(ApiKey, "the API key must never reach a response body");
        raw.Should().NotContain("api_key", "not even as a member name");
        var dto = JsonDocument.Parse(raw).RootElement.Clone();
        AssertDtoShape(dto);
        dto.GetProperty("id").GetGuid().Should().Be(modelId);
        dto.GetProperty("display_name").GetString().Should().Be(created.GetProperty("display_name").GetString());
        dto.GetProperty("base_url").GetString().Should().Be(fixture.BaseUrl);
        dto.GetProperty("tool_calling_probe_result").GetString().Should().Be("Pass");

        // Typed-record deserialization: the published ModelDto binds the body
        // exactly — a synthesized api_key member would break the property count.
        var typed = JsonSerializer.Deserialize(raw, AdminModelsJsonContext.Default.ModelDto);
        typed.Should().NotBeNull();
        typed!.Id.Should().Be(modelId);
        typeof(ModelDto).GetProperty("ApiKey").Should().BeNull("the projection has no key member");
    }

    [Fact]
    public async Task Get_Missing_Returns404()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;
        var missing = Guid.NewGuid();

        using var response = await client.SendAsync(
            AdminTestHost.Request(HttpMethod.Get, $"/v1/admin/models/{missing}", host.Bearer));

        var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.NotFound);
        problem.GetProperty("type").GetString().Should().Be("/errors/model-not-found");
        problem.GetProperty("detail").GetString().Should().Contain(missing.ToString());
    }

    // -----------------------------------------------------------------------
    // PATCH /v1/admin/models/{id}
    // -----------------------------------------------------------------------

    /// <summary>Task row: a base_url change re-probes over HTTP, fails, and updates NOTHING.</summary>
    [Fact]
    public async Task Patch_BaseUrlChanged_RerunsProbe_AndFails_NoFieldsUpdated()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;
        var marker = host.Marker;
        var created = await CreateModelAsync(host, CreateBody(marker));
        var modelId = created.GetProperty("id").GetGuid();
        var before = (await FindRowAsync(factory, modelId))!;
        before.Should().NotBeNull("the created row must exist before the patch");

        await fixture.SetScenarioAsync("probe-fail");
        var requestsBefore = TotalRequests();

        using var response = await client.SendAsync(AdminTestHost.Request(
            HttpMethod.Patch,
            $"/v1/admin/models/{modelId}",
            host.Bearer,
            PatchBody(displayName: $"{marker} renamed", baseUrl: "https://m3t3-alt.example/v1")));

        var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.BadRequest);
        problem.GetProperty("type").GetString().Should().Be("/errors/model-probe-failed");
        TotalRequests().Should().Be(
            requestsBefore + 1, "a changed base_url is a different cache key → the probe re-runs (SQ-M3-13)");

        // NO field is updated (task scope table).
        var after = (await FindRowAsync(factory, modelId))!;
        after.Should().NotBeNull();
        after.DisplayName.Should().Be(before.DisplayName, "display_name must be untouched on probe fail");
        after.BaseUrl.Should().Be(before.BaseUrl, "base_url must be untouched on probe fail");
        after.ModelName.Should().Be(before.ModelName);
        after.ApiKeySecretRef.Should().Be(before.ApiKeySecretRef, "no key rotation happened");
        after.ToolCallingProbedAt.Should().Be(before.ToolCallingProbedAt, "no probe stamp is written on fail");

        // The probe's fail row targets this model; no UpdateModel row exists.
        var failRows = await RecentAuditAsync(factory, AuditActions.ModelProbeFailed);
        failRows.Where(row => row.TargetId == modelId.ToString())
            .Should().ContainSingle("the PATCH-path fail row correlates to the catalog row");
        var updateRows = await RecentAuditAsync(factory, AuditActions.UpdateModel);
        updateRows.Where(row => row.TargetId == modelId.ToString()).Should().BeEmpty(
            "a rejected patch emits no UpdateModel row");
    }

    /// <summary>Task row: an api_key in the patch writes a NEW blob and deletes the old one (same transaction).</summary>
    [Fact]
    public async Task Patch_ApiKeyChanged_RewritesEncryptedBlob_SameTransaction()
    {
        const string rotatedKey = "sk-m3t3-rotated-key";
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;
        var created = await CreateModelAsync(host, CreateBody(host.Marker));
        var modelId = created.GetProperty("id").GetGuid();
        var before = (await FindRowAsync(factory, modelId))!;
        before.Should().NotBeNull("the created row must exist before the patch");
        before.ApiKeySecretRef.Should().NotBeNull();
        var oldBlobId = Guid.Parse(before.ApiKeySecretRef!);
        var requestsBefore = TotalRequests();

        using var response = await client.SendAsync(AdminTestHost.Request(
            HttpMethod.Patch, $"/v1/admin/models/{modelId}", host.Bearer, PatchBody(apiKey: rotatedKey)));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        TotalRequests().Should().Be(
            requestsBefore,
            "(base_url, model_name) is unchanged and the Pass row is fresh → PassFromCache, no HTTP");
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain(rotatedKey, "the API key must never reach a response body");
        var dto = JsonDocument.Parse(raw).RootElement.Clone();
        AssertDtoShape(dto);

        var after = (await FindRowAsync(factory, modelId))!;
        after.Should().NotBeNull("the patch must have updated the row");
        after.ApiKeySecretRef.Should().NotBe(
            before.ApiKeySecretRef, "the patch points the row at a freshly written blob");
        var newBlobId = Guid.Parse(after.ApiKeySecretRef!);
        newBlobId.Should().NotBe(oldBlobId);

        (await FindBlobAsync(factory, oldBlobId)).Should().BeNull(
            "the old encrypted_blobs row is deleted in the same transaction");
        var newBlob = (await FindBlobAsync(factory, newBlobId))!;
        newBlob.Should().NotBeNull("the new key blob must exist");
        newBlob.Purpose.Should().Be(EncryptedModelApiKeyStore.ModelApiKeyPurpose);
        newBlob.OwnerUserId.Should().BeNull();
        ContainsBytes(newBlob.Ciphertext, Encoding.UTF8.GetBytes(rotatedKey)).Should().BeFalse(
            "the rotated key is wrapped, never stored in clear");

        var auditRow = await SingleAuditAsync(factory, AuditActions.UpdateModel, modelId);
        auditRow.Hmac.Length.Should().Be(32);
        auditRow.MetadataJson.Should().NotContain(rotatedKey, "no key value in metadata_json");
        auditRow.MetadataJson.Should().NotContain(ApiKey, "no previous key value either");
        using var metadata = JsonDocument.Parse(auditRow.MetadataJson!);
        metadata.RootElement.GetProperty("model_id").GetGuid().Should().Be(modelId);
        metadata.RootElement.GetProperty("probe_outcome").GetString().Should().Be("PassFromCache");
        var changedFields = metadata.RootElement.GetProperty("changed_fields")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToList();
        changedFields.Should().Contain("api_key", "changed_fields names the members the PATCH body carried");

        // Review S3 — rollback, not just end state: fail the audit write of a
        // SECOND rotation while its transaction is open. The whole rotation
        // must roll back — ref unchanged, no blob churn.
        var blobsBeforeFailure = await CountModelKeyBlobsAsync(factory);
        _sabotageAction = AuditActions.UpdateModel;
        try
        {
            using var failed = await client.SendAsync(AdminTestHost.Request(
                HttpMethod.Patch,
                $"/v1/admin/models/{modelId}",
                host.Bearer,
                PatchBody(apiKey: "sk-m3t3-rotated-key-2")));
            failed.StatusCode.Should().Be(
                HttpStatusCode.InternalServerError,
                "the sabotaged audit write fails the request so the transaction can roll back");
        }
        finally
        {
            _sabotageAction = null;
        }

        var afterFailure = (await FindRowAsync(factory, modelId))!;
        afterFailure.Should().NotBeNull("the row itself must survive the rollback");
        afterFailure.ApiKeySecretRef.Should().Be(
            after.ApiKeySecretRef, "the rotation rolled back — the ref still points at the pre-failure blob");
        (await FindBlobAsync(factory, newBlobId)).Should().NotBeNull(
            "the blob the ref points at was never deleted by the failed rotation");
        (await CountModelKeyBlobsAsync(factory)).Should().Be(
            blobsBeforeFailure, "the new key blob insert rolled back with the transaction (review S3)");
    }

    /// <summary>Task row: a within-window display_name patch is served PassFromCache — no HTTP call.</summary>
    [Fact]
    public async Task Patch_DisplayNameChangedWithinWindow_ServedFromCache_NoHttpCall()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;
        var marker = host.Marker;
        var created = await CreateModelAsync(host, CreateBody(marker));
        var modelId = created.GetProperty("id").GetGuid();
        var requestsBefore = TotalRequests();

        using var response = await client.SendAsync(AdminTestHost.Request(
            HttpMethod.Patch,
            $"/v1/admin/models/{modelId}",
            host.Bearer,
            PatchBody(displayName: $"{marker} renamed")));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        TotalRequests().Should().Be(
            requestsBefore, "the within-window no-change patch must not reach the mock (SQ-M3-13)");
        var dto = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        AssertDtoShape(dto);
        dto.GetProperty("display_name").GetString().Should().Be($"{marker} renamed");

        var row = (await FindRowAsync(factory, modelId))!;
        row.Should().NotBeNull("the patched row must exist");
        row.DisplayName.Should().Be($"{marker} renamed");
        row.BaseUrl.Should().Be(fixture.BaseUrl, "only the patched member changes");

        var auditRow = await SingleAuditAsync(factory, AuditActions.UpdateModel, modelId);
        auditRow.MetadataJson.Should().NotContain("api_key", "no key member was part of this patch");
        using var metadata = JsonDocument.Parse(auditRow.MetadataJson!);
        metadata.RootElement.GetProperty("probe_outcome").GetString().Should().Be("PassFromCache");
        var changedFields = metadata.RootElement.GetProperty("changed_fields")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToList();
        changedFields.Should().Equal("display_name");
    }

    // -----------------------------------------------------------------------
    // DELETE /v1/admin/models/{id}
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Delete_CascadesEncryptedBlob_SameTransaction()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;
        var created = await CreateModelAsync(host, CreateBody(host.Marker));
        var modelId = created.GetProperty("id").GetGuid();
        var row = (await FindRowAsync(factory, modelId))!;
        row.Should().NotBeNull("the created row must exist before DELETE");
        var blobId = Guid.Parse(row.ApiKeySecretRef!);

        using var response = await client.SendAsync(
            AdminTestHost.Request(HttpMethod.Delete, $"/v1/admin/models/{modelId}", host.Bearer));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, "DELETE answers 204");
        (await FindRowAsync(factory, modelId)).Should().BeNull("the catalog row is gone");
        (await FindBlobAsync(factory, blobId)).Should().BeNull(
            "the referenced encrypted_blobs row is removed in the same transaction");

        var auditRow = await SingleAuditAsync(factory, AuditActions.DeleteModel, modelId);
        auditRow.Hmac.Length.Should().Be(32, "audit rows are HMAC-chained (Q-O2)");
        auditRow.TargetType.Should().Be("model_catalog");
        auditRow.ActorUserId.Should().Be(host.AdminId);
        auditRow.MetadataJson.Should().NotContain(ApiKey);
        auditRow.MetadataJson.Should().NotContain("api_key");
        using var metadata = JsonDocument.Parse(auditRow.MetadataJson!);
        metadata.RootElement.GetProperty("model_id").GetGuid().Should().Be(modelId);
        metadata.RootElement.GetProperty("display_name").GetString()
            .Should().Be(created.GetProperty("display_name").GetString());
        metadata.RootElement.GetProperty("provider").GetString().Should().Be(Provider);

        // Review S3 — rollback, not just end state: fail the audit write of a
        // SECOND delete while its transaction is open. Both the catalog row and
        // the referenced key blob must survive untouched.
        var rollback = await CreateModelAsync(host, CreateBody(host.Marker));
        var rollbackId = rollback.GetProperty("id").GetGuid();
        var rollbackRow = (await FindRowAsync(factory, rollbackId))!;
        rollbackRow.Should().NotBeNull();
        var rollbackBlobId = Guid.Parse(rollbackRow.ApiKeySecretRef!);
        var blobsBeforeFailure = await CountModelKeyBlobsAsync(factory);

        _sabotageAction = AuditActions.DeleteModel;
        try
        {
            using var failed = await client.SendAsync(
                AdminTestHost.Request(HttpMethod.Delete, $"/v1/admin/models/{rollbackId}", host.Bearer));
            failed.StatusCode.Should().Be(
                HttpStatusCode.InternalServerError,
                "the sabotaged audit write fails the request so the transaction can roll back");
        }
        finally
        {
            _sabotageAction = null;
        }

        (await FindRowAsync(factory, rollbackId)).Should().NotBeNull(
            "the row delete rolled back with the failed audit write (review S3)");
        (await FindBlobAsync(factory, rollbackBlobId)).Should().NotBeNull(
            "the blob delete rolled back with the failed audit write (review S3)");
        (await CountModelKeyBlobsAsync(factory)).Should().Be(
            blobsBeforeFailure, "no encrypted_blobs row may change across the rolled-back delete");
    }

    // -----------------------------------------------------------------------
    // GET /v1/admin/models — cursor + filters (§13.13 / SQ-M3-2)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task List_CursorAdvances_AcrossPages()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;
        var marker = host.Marker;
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var one = await SeedRowAsync(host, $"{marker} one", createdAt: createdAt);
        var two = await SeedRowAsync(host, $"{marker} two", createdAt: createdAt.AddMinutes(-1));
        var three = await SeedRowAsync(host, $"{marker} three", createdAt: createdAt.AddMinutes(-2));
        var filter = $"filter%5Bdisplay_name_contains%5D={Uri.EscapeDataString(marker)}";

        using var pageOneResponse = await client.SendAsync(
            AdminTestHost.Request(HttpMethod.Get, $"/v1/admin/models?limit=2&{filter}", host.Bearer));
        pageOneResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var pageOneRaw = await pageOneResponse.Content.ReadAsStringAsync();
        pageOneRaw.Should().NotContain(ApiKey);
        var pageOne = JsonDocument.Parse(pageOneRaw).RootElement.Clone();
        pageOne.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid()).Should().Equal(
                [one, two],
                "§13.13: (created_at DESC, id DESC) — newest first");
        pageOne.TryGetProperty("next_cursor", out var cursorToken).Should().BeTrue(
            "a further page exists, so next_cursor is present");
        var cursor = cursorToken.GetString();
        cursor.Should().NotBeNullOrWhiteSpace();

        using var pageTwoResponse = await client.SendAsync(AdminTestHost.Request(
            HttpMethod.Get,
            $"/v1/admin/models?limit=2&cursor={Uri.EscapeDataString(cursor!)}&{filter}",
            host.Bearer));
        pageTwoResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var pageTwo = await AccountTestHost.ReadJsonAsync(pageTwoResponse);
        pageTwo.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid()).Should().Equal(three);
        pageTwo.TryGetProperty("next_cursor", out _).Should().BeFalse(
            "the last page omits next_cursor (§13.13)");

        // Review S1: a crafted NON-UTC cursor decodes to a UTC instant — Npgsql
        // rejects Offset != 0 for timestamptz, so without normalization this
        // would be a 500 instead of a §13.12 success/400.
        var nonUtcCursor = Cursor.Encode(
            DateTimeOffset.UtcNow.AddMinutes(-2).ToOffset(TimeSpan.FromHours(2)),
            Guid.NewGuid());
        using var offsetResponse = await client.SendAsync(AdminTestHost.Request(
            HttpMethod.Get,
            $"/v1/admin/models?limit=2&cursor={Uri.EscapeDataString(nonUtcCursor)}&{filter}",
            host.Bearer));
        offsetResponse.StatusCode.Should().Be(
            HttpStatusCode.OK, "a +02:00 cursor is normalized to UTC — never a 500 (review S1)");

        // Review S2: a garbage cursor is a 400 /errors/validation naming `cursor`.
        using var badCursorResponse = await client.SendAsync(AdminTestHost.Request(
            HttpMethod.Get,
            $"/v1/admin/models?cursor={Uri.EscapeDataString("%%%not-a-cursor%%%")}",
            host.Bearer));
        var badCursorReason = await SourcesTestHost.ReadErrorReasonAsync(
            badCursorResponse, HttpStatusCode.BadRequest, "/errors/validation", "cursor");
        badCursorReason.Should().NotBeNullOrWhiteSpace(
            "the malformed cursor must surface as the errors[] entry for `cursor` (review S2)");
    }

    [Fact]
    public async Task List_FilterByEnabled_True_ReturnsOnlyEnabled()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;
        var marker = host.Marker;
        var enabledA = await SeedRowAsync(host, $"{marker} enabled-a");
        var enabledB = await SeedRowAsync(host, $"{marker} enabled-b");
        var disabled = await SeedRowAsync(host, $"{marker} disabled", enabled: false);
        var filter = $"filter%5Bdisplay_name_contains%5D={Uri.EscapeDataString(marker)}";

        using var enabledResponse = await client.SendAsync(
            AdminTestHost.Request(HttpMethod.Get, $"/v1/admin/models?filter%5Benabled%5D=true&{filter}", host.Bearer));
        enabledResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var enabledPage = await AccountTestHost.ReadJsonAsync(enabledResponse);
        enabledPage.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .Should().BeEquivalentTo([enabledA, enabledB]);
        enabledPage.GetProperty("items").EnumerateArray()
            .All(item => item.GetProperty("enabled").GetBoolean()).Should().BeTrue(
                "filter[enabled]=true must never return a disabled row");

        using var disabledResponse = await client.SendAsync(
            AdminTestHost.Request(HttpMethod.Get, $"/v1/admin/models?filter%5Benabled%5D=false&{filter}", host.Bearer));
        disabledResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var disabledPage = await AccountTestHost.ReadJsonAsync(disabledResponse);
        disabledPage.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid()).Should().Equal(disabled);
    }

    [Fact]
    public async Task List_FilterByProvider_OpenaiCompatible_ReturnsOnlyMatching()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;
        var marker = host.Marker;
        var openAiA = await SeedRowAsync(host, $"{marker} openai-a");
        var openAiB = await SeedRowAsync(host, $"{marker} openai-b");
        await SeedRowAsync(host, $"{marker} acme", provider: "acme-llm");
        var filter = $"filter%5Bdisplay_name_contains%5D={Uri.EscapeDataString(marker)}";

        using var response = await client.SendAsync(AdminTestHost.Request(
            HttpMethod.Get,
            $"/v1/admin/models?filter%5Bprovider%5D={Provider}&{filter}",
            host.Bearer));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await AccountTestHost.ReadJsonAsync(response);
        var items = page.GetProperty("items").EnumerateArray().ToList();
        items.Select(item => item.GetProperty("id").GetGuid())
            .Should().BeEquivalentTo([openAiA, openAiB]);
        items.Select(item => item.GetProperty("provider").GetString())
            .Should().OnlyContain(provider => provider == Provider);
    }

    [Fact]
    public async Task List_LimitOverMax_Returns400()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;

        using var response = await client.SendAsync(
            AdminTestHost.Request(HttpMethod.Get, "/v1/admin/models?limit=101", host.Bearer));

        var reason = await SourcesTestHost.ReadErrorReasonAsync(
            response, HttpStatusCode.BadRequest, "/errors/validation", "limit");
        reason.Should().Contain("100", "§13.13 caps limit at 100 and names the field");
    }

    // -----------------------------------------------------------------------
    // Authorization + §13.14 rate-limit headers
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AsNonAdmin_Returns403()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;

        // Anonymous → 401 RFC 7807 (JwtBearer challenge event).
        using var anonymousResponse = await client.GetAsync("/v1/admin/models");
        (await AccountTestHost.ReadProblemAsync(anonymousResponse, HttpStatusCode.Unauthorized))
            .GetProperty("type").GetString().Should().Be("/errors/unauthorized");

        // Authenticated non-admin → 403 on read and write (SQ-M3-1).
        var userId = await AdminTestHost.SeedUserAsync(
            factory, $"m3t3-nonadmin-{Guid.NewGuid():N}@example.test", UserRole.User);
        var userBearer = await AdminTestHost.BearerAsync(factory, userId, "User");

        using var forbiddenGet = await client.SendAsync(
            AdminTestHost.Request(HttpMethod.Get, "/v1/admin/models", userBearer));
        (await AccountTestHost.ReadProblemAsync(forbiddenGet, HttpStatusCode.Forbidden))
            .GetProperty("type").GetString().Should().Be("/errors/forbidden");

        using var forbiddenPost = await client.SendAsync(
            AdminTestHost.Request(
                HttpMethod.Post, "/v1/admin/models", userBearer, CreateBody(host.Marker)));
        (await AccountTestHost.ReadProblemAsync(forbiddenPost, HttpStatusCode.Forbidden))
            .GetProperty("type").GetString().Should().Be("/errors/forbidden");

        // …and the three by-id routes (review N1: the gate is route-wide, so
        // every route in the group must refuse the non-admin caller).
        using var forbiddenGetById = await client.SendAsync(
            AdminTestHost.Request(HttpMethod.Get, $"/v1/admin/models/{Guid.NewGuid()}", userBearer));
        (await AccountTestHost.ReadProblemAsync(forbiddenGetById, HttpStatusCode.Forbidden))
            .GetProperty("type").GetString().Should().Be("/errors/forbidden");

        using var forbiddenPatch = await client.SendAsync(
            AdminTestHost.Request(
                HttpMethod.Patch,
                $"/v1/admin/models/{Guid.NewGuid()}",
                userBearer,
                PatchBody(displayName: $"{host.Marker} renamed")));
        (await AccountTestHost.ReadProblemAsync(forbiddenPatch, HttpStatusCode.Forbidden))
            .GetProperty("type").GetString().Should().Be("/errors/forbidden");

        using var forbiddenDelete = await client.SendAsync(
            AdminTestHost.Request(HttpMethod.Delete, $"/v1/admin/models/{Guid.NewGuid()}", userBearer));
        (await AccountTestHost.ReadProblemAsync(forbiddenDelete, HttpStatusCode.Forbidden))
            .GetProperty("type").GetString().Should().Be("/errors/forbidden");

        // …while the seeded Admin walks right through (role plumbing proof).
        using var allowed = await client.SendAsync(
            AdminTestHost.Request(HttpMethod.Get, "/v1/admin/models", host.Bearer));
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task All_AuthenticatedResponses_EmitXRateLimitHeaders()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;
        var marker = host.Marker;

        using var createResponse = await client.SendAsync(
            AdminTestHost.Request(HttpMethod.Post, "/v1/admin/models", host.Bearer, CreateBody(marker)));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var modelId = (await AccountTestHost.ReadJsonAsync(createResponse)).GetProperty("id").GetGuid();
        AssertRateLimitHeaders(createResponse, "POST 201");

        using var listResponse = await client.SendAsync(
            AdminTestHost.Request(HttpMethod.Get, "/v1/admin/models", host.Bearer));
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertRateLimitHeaders(listResponse, "GET list 200");

        using var getResponse = await client.SendAsync(
            AdminTestHost.Request(HttpMethod.Get, $"/v1/admin/models/{modelId}", host.Bearer));
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertRateLimitHeaders(getResponse, "GET 200");

        using var patchResponse = await client.SendAsync(AdminTestHost.Request(
            HttpMethod.Patch,
            $"/v1/admin/models/{modelId}",
            host.Bearer,
            PatchBody(displayName: $"{marker} renamed")));
        patchResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertRateLimitHeaders(patchResponse, "PATCH 200");

        using var notFoundResponse = await client.SendAsync(
            AdminTestHost.Request(HttpMethod.Get, $"/v1/admin/models/{Guid.NewGuid()}", host.Bearer));
        notFoundResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        AssertRateLimitHeaders(notFoundResponse, "GET 404 problem document");

        using var badRequestResponse = await client.SendAsync(
            AdminTestHost.Request(HttpMethod.Get, "/v1/admin/models?limit=101", host.Bearer));
        badRequestResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertRateLimitHeaders(badRequestResponse, "GET 400 validation problem document");

        using var deleteResponse = await client.SendAsync(
            AdminTestHost.Request(HttpMethod.Delete, $"/v1/admin/models/{modelId}", host.Bearer));
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        AssertRateLimitHeaders(deleteResponse, "DELETE 204");
    }

    // -----------------------------------------------------------------------
    // Lifecycle (xUnit instantiates the class once per test) + per-test cleanup
    // -----------------------------------------------------------------------

    /// <summary>No per-test setup — <see cref="CreateHostAsync"/> is the setup seam.</summary>
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Per-test cleanup on the SHARED container database: the model_catalog
    /// rows this test created (marker prefix) plus the key blobs they
    /// reference. Without it, the classes asserting a zero-row model_catalog
    /// downstream (e.g. SourcesFetchNowTests.M2_NoLlmCall_Happens,
    /// M2EndToEndTests) fail whenever they run after this suite. Audit rows
    /// are deliberately left in place — audit_log is append-only (the HMAC
    /// chain spans the shared table, so deletion would break
    /// VerifyChainAsync assertions) and every audit assertion here correlates
    /// by marker or model id.
    /// </summary>
    public async Task DisposeAsync()
    {
        var marker = _marker;
        if (marker is null)
        {
            return;
        }

        await AccountTestHost.MigrateAsync(postgres);
        await using var db = new EmailAgentDbContext(
            new DbContextOptionsBuilder<EmailAgentDbContext>()
                .UseNpgsql(postgres.ConnectionString)
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
    // Host + engine seam
    // -----------------------------------------------------------------------

    private sealed record ModelHost(
        WebApplicationFactory<Program> Factory,
        HttpClient Client,
        Guid AdminId,
        string Bearer,
        string Marker);

    /// <summary>
    /// The real audit sink with one failure switch (review S3): when the
    /// sabotaged action is logged the write is refused BEFORE it reaches the
    /// table, so the surrounding transaction rolls back every mutation it
    /// already made — exactly the atomicity the task requires of
    /// create/PATCH/DELETE.
    /// </summary>
    private sealed class SabotageAuditLogger(IAuditLogger inner, Func<string, bool> shouldFail) : IAuditLogger
    {
        public Task LogAsync(AuditEvent evt, CancellationToken ct)
        {
            if (shouldFail(evt.Action))
            {
                throw new InvalidOperationException(
                    $"M3T3 test sabotage: the '{evt.Action}' audit write failed.");
            }

            return inner.LogAsync(evt, ct);
        }

        public Task<long?> VerifyChainAsync(CancellationToken ct) => inner.VerifyChainAsync(ct);
    }

    /// <summary>
    /// One host per test: migrated shared Postgres, a seeded Admin with a
    /// real issuer-minted token, the probe pinned to this class's OpenAIMock
    /// and a fresh unique marker (list-isolation + cleanup key).
    /// </summary>
    private async Task<ModelHost> CreateHostAsync()
    {
        var marker = $"m3t3-{Guid.NewGuid():N}";
        _marker = marker;

        // Deterministic mock state before any probe can run (one fixture per class).
        await fixture.SetScenarioAsync("probe-pass");

        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        var factory = ApiTestFactory.Create(connectionString)
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                // M3T3 test seam: the probe resolves Func<IAgentEngine> per
                // call; pinning it to the in-process OpenAIMock's TestServer
                // client is what keeps every probe off the network (AGENTS.md
                // test constraint). The TestServer client answers ANY host, so
                // a patched base_url still lands on this fixture's active
                // scenario (recorded deviation: one fixture per class, not a
                // second mock instance).
                services.RemoveAll<Func<IAgentEngine>>();
                Func<IAgentEngine> engine = () => new OpenAICompatibleAgentEngine(fixture.Client);
                services.AddSingleton(engine);

                // Review S3 seam: wrap the REAL audit sink so a test can make
                // exactly one audit action fail inside an open transaction and
                // prove the catalog row + key blob roll back together (the
                // decorator delegates every non-sabotaged action unchanged).
                services.RemoveAll<IAuditLogger>();
                services.AddScoped<IAuditLogger>(provider =>
                    new SabotageAuditLogger(
                        ActivatorUtilities.CreateInstance<AuditLogger>(provider),
                        action => string.Equals(
                            action, _sabotageAction, StringComparison.Ordinal)));
            }));
        var client = AccountTestHost.CreateClient(factory);
        var adminId = await AdminTestHost.SeedUserAsync(
            factory, $"{marker}@example.test", UserRole.Admin);
        var bearer = await AdminTestHost.BearerAsync(factory, adminId, "Admin");
        return new ModelHost(factory, client, adminId, bearer, marker);
    }

    // -----------------------------------------------------------------------
    // Request/response helpers
    // -----------------------------------------------------------------------

    private object CreateBody(
        string marker,
        string? apiKey = ApiKey,
        string? displayName = null,
        string? baseUrl = null,
        string? modelName = null,
        bool enabled = true)
        => new
        {
            display_name = displayName ?? $"{marker} model",
            provider = Provider,
            base_url = baseUrl ?? fixture.BaseUrl,
            model_name = modelName ?? marker,
            api_key = apiKey,
            price_per_1k_input_usd = 0.0015m,
            price_per_1k_output_usd = 0.002m,
            enabled,
        };

    /// <summary>PATCH body: null members serialize as JSON null = "leave unchanged".</summary>
    private static object PatchBody(
        string? displayName = null, string? baseUrl = null, string? apiKey = null)
        => new { display_name = displayName, base_url = baseUrl, api_key = apiKey };

    /// <summary>POST asserts 201 + Location and returns the key-free ModelDto.</summary>
    private static async Task<JsonElement> CreateModelAsync(ModelHost host, object body)
    {
        using var response = await host.Client.SendAsync(
            AdminTestHost.Request(HttpMethod.Post, "/v1/admin/models", host.Bearer, body));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();
        var dto = await AccountTestHost.ReadJsonAsync(response);
        AssertDtoShape(dto);
        return dto;
    }

    /// <summary>The fixed 12-member projection — no api_key member, ever.</summary>
    private static void AssertDtoShape(JsonElement dto)
    {
        dto.ValueKind.Should().Be(JsonValueKind.Object, "every catalog response is a bare ModelDto");
        dto.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo(
                DtoMembers,
                "ModelDto is the fixed §13.13 projection — no api_key, no api_key_secret_ref, no encrypted_blob_id");
        typeof(ModelDto).GetProperties(BindingFlags.Public | BindingFlags.Instance).Length
            .Should().Be(DtoMembers.Length, "the typed record matches the wire projection exactly");
        typeof(ModelDto).GetProperty("ApiKey").Should().BeNull("typed-record proof: no ApiKey property");
    }

    /// <summary>§13.14: every authenticated response carries X-RateLimit-Limit/-Remaining/-Reset.</summary>
    private static void AssertRateLimitHeaders(HttpResponseMessage response, string what)
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

    /// <summary>Engine round-trips against this fixture's mock, summed over scenarios.</summary>
    private int TotalRequests() => fixture.RequestCounts.Values.Sum();

    // -----------------------------------------------------------------------
    // Database helpers
    // -----------------------------------------------------------------------

    private async Task<Guid> SeedRowAsync(
        ModelHost host,
        string displayName,
        string? modelName = null,
        string provider = Provider,
        bool enabled = true,
        DateTimeOffset? createdAt = null,
        ToolCallingProbeResult? probeResult = null,
        DateTimeOffset? probedAt = null)
    {
        using var scope = host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var id = Guid.NewGuid();
        db.ModelCatalog.Add(
            new ModelCatalogEntry
            {
                Id = id,
                DisplayName = displayName,
                Provider = provider,
                BaseUrl = fixture.BaseUrl,
                ModelName = modelName ?? host.Marker,
                Enabled = enabled,
                CreatedBy = host.AdminId,
                CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
                ToolCallingProbeResult = probeResult,
                ToolCallingProbedAt = probedAt,
                ApiKeySecretRef = null,
            });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<ModelCatalogEntry?> FindRowAsync(
        WebApplicationFactory<Program> factory, Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return await db.ModelCatalog.AsNoTracking().SingleOrDefaultAsync(model => model.Id == id);
    }

    private static async Task<List<ModelCatalogEntry>> RowsNamedAsync(
        WebApplicationFactory<Program> factory, string modelName)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return await db.ModelCatalog.AsNoTracking()
            .Where(model => model.ModelName == modelName)
            .ToListAsync();
    }

    private static async Task<EncryptedBlob?> FindBlobAsync(
        WebApplicationFactory<Program> factory, Guid blobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return await db.EncryptedBlobs.AsNoTracking()
            .SingleOrDefaultAsync(blob => blob.Id == blobId);
    }

    private static async Task<int> CountModelKeyBlobsAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return await db.EncryptedBlobs.CountAsync(
            blob => blob.Purpose == EncryptedModelApiKeyStore.ModelApiKeyPurpose);
    }

    /// <summary>The newest rows of one action on the shared container database (correlate in-memory by marker/id).</summary>
    private static async Task<List<AuditLog>> RecentAuditAsync(
        WebApplicationFactory<Program> factory, string action)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return await db.AuditLog.AsNoTracking()
            .Where(row => row.Action == action)
            .OrderByDescending(row => row.Id)
            .Take(100)
            .ToListAsync();
    }

    /// <summary>The single audit row of <paramref name="action"/> targeting this model id.</summary>
    private static async Task<AuditLog> SingleAuditAsync(
        WebApplicationFactory<Program> factory, string action, Guid modelId)
    {
        var rows = await RecentAuditAsync(factory, action);
        var matches = rows.Where(row => row.TargetId == modelId.ToString()).ToList();
        matches.Should().ContainSingle(
            $"exactly one {action} row must target {modelId} — found {matches.Count}");
        return matches[0];
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

    /// <summary>
    /// The probe-fail excerpt the reconstruction produces from the fixture
    /// (content + empty tool_calls + usage 4/9) — serialized with the shared
    /// options so the assertion is byte-exact for any encoder policy.
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
}
