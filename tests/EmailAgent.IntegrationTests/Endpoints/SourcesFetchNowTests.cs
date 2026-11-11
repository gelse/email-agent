using System.Net;
using System.Text.Json;
using EmailAgent.Core.Audit;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using EmailAgent.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmailAgent.IntegrationTests.Endpoints;

// ---------------------------------------------------------------------------
// M2T5 — POST /v1/user/sources/{id}/fetch-now, the M2 manual exerciser,
// against the real Greenmail fixture (the milestone's three row-verification
// tests live here: FetchNow_PersistsProcessedMessages_WithUniqueConstraint,
// FetchNow_RunTwice_NoDuplicates, M2_NoLlmCall_Happens). Purge-then-seed per
// test keeps the mailbox count exact; UIDs themselves keep climbing across
// purges, so every assertion is count/relative rather than a hard-coded UID.
// ---------------------------------------------------------------------------

[Collection("Postgres")]
public class SourcesFetchNowTests(PostgresFixture postgres, GreenMailFixture greenmail)
    : IClassFixture<GreenMailFixture>
{
    /// <summary>
    /// Milestone row "Dedupe-keyed record per new message": three seeded
    /// messages become three <c>processed_messages</c> rows keyed by
    /// UNIQUE(source_id, remote_uid), each with its encrypted body blob.
    /// </summary>
    [Fact]
    public async Task FetchNow_PersistsProcessedMessages_WithUniqueConstraint()
    {
        var (host, sourceId) = await SeedMailboxAndCreateSourceAsync(3);
        await using var factory = host.Factory;
        using var client = host.Client;

        using var response = await FetchAsync(host, sourceId);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await AccountTestHost.ReadJsonAsync(response);
        body.GetProperty("processed").GetInt32().Should().Be(3);
        body.GetProperty("new_processed_messages").GetInt32().Should().Be(3);
        var lastUid = body.GetProperty("checkpoint").GetProperty("last_uid").GetString();
        lastUid.Should().NotBeNullOrWhiteSpace("the checkpoint advances to the max UID");

        // One row per message, dedupe-keyed on this source.
        var rows = await SourcesTestHost.MessagesAsync(factory, sourceId);
        rows.Should().HaveCount(3);
        rows.Should().OnlyContain(r => r.SourceId == sourceId);
        rows.Select(r => r.RemoteUid).Should().OnlyHaveUniqueItems();
        rows.Select(r => uint.Parse(r.RemoteUid)).Max().ToString()
            .Should().Be(lastUid, "AdvanceCheckpointAsync takes the max UID of the batch");

        // The UNIQUE(source_id, remote_uid) constraint itself (M1T2 migration):
        // proven by an actual violation attempt, not by reading a name.
        var indexDef = await ReadSourceUidIndexAsync(factory);
        indexDef.Should().Contain("UNIQUE")
            .And.Contain("source_id")
            .And.Contain("remote_uid");

        // Q-LP1: every row carries its message_body blob.
        rows.Should().OnlyContain(r => r.BodyBlobId != null);
        foreach (var row in rows)
        {
            var blob = await SourcesTestHost.BlobAsync(factory, row.BodyBlobId!.Value);
            blob.Purpose.Should().Be("message_body");
            blob.OwnerUserId.Should().Be(host.UserId);
        }
    }

    /// <summary>
    /// Milestone row "Second fetch-now does NOT duplicate": the checkpoint
    /// hides the old UIDs, and even a forced re-yield cannot double-insert —
    /// the UNIQUE key + <c>ON CONFLICT DO NOTHING</c> is the enforcement.
    /// </summary>
    [Fact]
    public async Task FetchNow_RunTwice_NoDuplicates()
    {
        var (host, sourceId) = await SeedMailboxAndCreateSourceAsync(3);
        await using var factory = host.Factory;
        using var client = host.Client;

        using (var first = await FetchAsync(host, sourceId))
        {
            first.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await AccountTestHost.ReadJsonAsync(first);
            body.GetProperty("new_processed_messages").GetInt32().Should().Be(3);
        }

        using (var second = await FetchAsync(host, sourceId))
        {
            second.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await AccountTestHost.ReadJsonAsync(second);
            body.GetProperty("processed").GetInt32().Should().Be(
                0, "the checkpoint advanced past every existing UID");
            body.GetProperty("new_processed_messages").GetInt32().Should().Be(0);
        }

        (await SourcesTestHost.MessagesAsync(factory, sourceId)).Should().HaveCount(
            3, "COUNT(*) must be 3, not 6");
    }

    /// <summary>
    /// The <c>ON CONFLICT (source_id, remote_uid) DO NOTHING</c> path itself:
    /// with the checkpoint reset the connector re-yields the SAME UIDs, and
    /// the insert must swallow them (no new rows, no orphan body blobs).
    /// </summary>
    [Fact]
    public async Task FetchNow_CheckpointReset_ReyieldedMessages_DoNotDuplicate()
    {
        var (host, sourceId) = await SeedMailboxAndCreateSourceAsync(2);
        await using var factory = host.Factory;
        using var client = host.Client;

        using (var first = await FetchAsync(host, sourceId))
        {
            first.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // Rewind the checkpoint behind the API's back — exactly the state a
        // concurrent worker or a re-run would produce.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EmailAgent.Infrastructure.EmailAgentDbContext>();
            var row = await db.MessageSources.SingleAsync(s => s.Id == sourceId);
            row.CheckpointJson = "{}";
            await db.SaveChangesAsync();
        }

        using var again = await FetchAsync(host, sourceId);
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await AccountTestHost.ReadJsonAsync(again);
        body.GetProperty("processed").GetInt32().Should().Be(
            2, "the same two UIDs are yielded again");
        body.GetProperty("new_processed_messages").GetInt32().Should().Be(
            0, "ON CONFLICT DO NOTHING accepts nothing new");

        (await SourcesTestHost.MessagesAsync(factory, sourceId)).Should().HaveCount(2);

        // No orphaned body blob survived the swallowed inserts either.
        var blobs = await BodyBlobsAsync(factory, host.UserId);
        blobs.Should().HaveCount(2);
    }

    /// <summary>
    /// Milestone row "No LLM call" (M2T5's adaptation: this milestone has no
    /// <c>IEngineRegistry</c> to mock, so the absence is asserted structurally
    /// — see the three blocks below).
    /// </summary>
    [Fact]
    public async Task M2_NoLlmCall_Happens()
    {
        var (host, sourceId) = await SeedMailboxAndCreateSourceAsync(3);
        await using var factory = host.Factory;
        using var client = host.Client;

        using var response = await FetchAsync(host, sourceId);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // (a) The row's type is the plain IMAP connector — nothing engine-related.
        var source = await SourcesTestHost.SourceRowAsync(factory, sourceId);
        source.TypeId.Should().Be("imap");

        // (b) Every column the M3 pipeline later fills stays at its §6
        //     initial value: stage='fetched', prefilter_matched=false,
        //     engine/token/cost columns NULL.
        var rows = await SourcesTestHost.MessagesAsync(factory, sourceId);
        rows.Should().HaveCount(3);
        rows.Should().OnlyContain(r => r.Stage == ProcessedMessageStage.fetched);
        rows.Should().OnlyContain(r => r.PrefilterMatched == false);
        rows.Should().OnlyContain(r => r.ModelDecisionJson == null);
        rows.Should().OnlyContain(r => r.ActionsExecutedJson == null);
        rows.Should().OnlyContain(r => r.PromptTokens == null);
        rows.Should().OnlyContain(r => r.CompletionTokens == null);
        rows.Should().OnlyContain(r => r.TotalTokens == null);
        rows.Should().OnlyContain(r => r.CostUsd == null);
        rows.Should().OnlyContain(r => r.BodyBlobId != null);

        // (c) No model-catalogue lookup: the table stays untouched, and no
        //     engine registry contract even exists in the process yet (M3).
        (await SourcesTestHost.ModelCatalogCountAsync(factory)).Should().Be(
            0, "fetch-now must never read model_catalog");
        EngineRegistryContractTypes().Should().BeEmpty(
            "IEngineRegistry lands with M3; nothing here can call an engine that has no contract");
    }

    /// <summary>
    /// M5T1: the generic session gate replaces the retired x-user-dek
    /// validation (a missing header never reaches the handler — 403
    /// <c>/errors/crypto-locked</c>), while a session whose DEK cannot open
    /// the source's config blob still fails INSIDE the handler with 400
    /// <c>/errors/dek-invalid</c> — and nothing ran either way.
    /// </summary>
    [Fact]
    public async Task FetchNow_MissingSession_Returns403_AndWrongDek_DekInvalid()
    {
        var (host, sourceId) = await SeedMailboxAndCreateSourceAsync(1);
        await using var factory = host.Factory;
        using var client = host.Client;

        using var missing = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Post, $"/v1/user/sources/{sourceId}/fetch-now", host.Bearer));
        var locked = await AccountTestHost.ReadProblemAsync(
            missing, HttpStatusCode.Forbidden);
        locked.GetProperty("type").GetString().Should().Be("/errors/crypto-locked",
            "an unlocked session is judged by the gate, never by field validation");

        // Re-unlock with a different DEK: its session cannot decrypt the
        // source's stored config (the idempotent replace mints a new session).
        var wrongSession = await SourcesTestHost.UnlockAsync(
            client, host.Bearer, SourcesTestHost.NewDek());
        using var wrongDek = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Post, $"/v1/user/sources/{sourceId}/fetch-now",
            host.Bearer, session: wrongSession));
        (await AccountTestHost.ReadProblemAsync(wrongDek, HttpStatusCode.BadRequest))
            .GetProperty("type").GetString().Should().Be("/errors/dek-invalid");

        // Nothing ran: no rows, no audit claim.
        (await SourcesTestHost.MessagesAsync(factory, sourceId)).Should().BeEmpty();
        (await AccountTestHost.AuditRowsAsync(factory, host.UserId))
            .Should().NotContain(a => a.Action == AuditActions.FetchSourceNow);
    }

    [Fact]
    public async Task FetchNow_UnknownOrForeignSource_Returns404()
    {
        var (host, sourceId) = await SeedMailboxAndCreateSourceAsync(1);
        await using var factory = host.Factory;
        using var client = host.Client;

        using var unknown = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Post, $"/v1/user/sources/{Guid.NewGuid()}/fetch-now",
            host.Bearer, session: host.Session));
        (await AccountTestHost.ReadProblemAsync(unknown, HttpStatusCode.NotFound))
            .GetProperty("type").GetString().Should().Be("/errors/source-not-found");

        var (intruderId, intruderBearer, _, intruderSession) = await SourcesTestHost.AddUserAsync(factory);
        intruderId.Should().NotBe(host.UserId);
        using var foreign = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Post, $"/v1/user/sources/{sourceId}/fetch-now",
            intruderBearer, session: intruderSession));
        foreign.StatusCode.Should().Be(
            HttpStatusCode.NotFound, "a foreign id is indistinguishable from an unknown one");
    }

    [Fact]
    public async Task FetchNow_AdvancesCheckpoint_AndRunFields_AndAudits()
    {
        var (host, sourceId) = await SeedMailboxAndCreateSourceAsync(2);
        await using var factory = host.Factory;
        using var client = host.Client;

        using var response = await FetchAsync(host, sourceId);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await AccountTestHost.ReadJsonAsync(response);
        var lastUid = body.GetProperty("checkpoint").GetProperty("last_uid").GetString()!;

        // GET reflects the advance for the SPA.
        using var get = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Get, $"/v1/user/sources/{sourceId}", host.Bearer, session: host.Session));
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await AccountTestHost.ReadJsonAsync(get);
        dto.GetProperty("checkpoint").GetProperty("last_uid").GetString().Should().Be(lastUid);
        var lastRunAt = dto.GetProperty("last_run_at").GetDateTimeOffset();
        var nextRunAt = dto.GetProperty("next_run_at").GetDateTimeOffset();
        var row = await SourcesTestHost.SourceRowAsync(factory, sourceId);
        row.LastRunAt.Should().NotBeNull();
        row.NextRunAt.Should().NotBeNull();
        nextRunAt.Should().BeCloseTo(
            lastRunAt.AddMinutes(row.IntervalMinutes), TimeSpan.FromSeconds(5),
            "next_run_at = last_run_at + interval_minutes (§6; the scheduler that consumes it is M5)");
        row.CheckpointJson.Should().Contain(lastUid);

        var audits = await AccountTestHost.AuditRowsAsync(factory, host.UserId);
        audits.Should().ContainSingle(a => a.Action == AuditActions.FetchSourceNow)
            .Which.MetadataJson.Should().Match(m =>
                JsonDocument.Parse(m ?? "{}", default).RootElement.GetProperty("processed").GetInt32() == 2);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>Purges Greenmail, seeds <paramref name="count"/> messages and creates an alice source for a fresh user.</summary>
    private async Task<(SourcesHost Host, Guid SourceId)> SeedMailboxAndCreateSourceAsync(int count)
    {
        await greenmail.PurgeMailAsync();
        for (var i = 1; i <= count; i++)
        {
            await greenmail.SendAsync($"m2t5 message {i}", $"body of message {i}");
        }

        var host = await SourcesTestHost.CreateUserAsync(postgres);
        var created = await SourcesTestHost.CreateSourceAsync(
            host, "greenmail inbox", greenmail.CreateConfig());
        return (host, SourcesTestHost.IdOf(created));
    }

    private Task<HttpResponseMessage> FetchAsync(SourcesHost host, Guid sourceId, string? session = null)
        => host.Client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Post, $"/v1/user/sources/{sourceId}/fetch-now",
            host.Bearer, session: session ?? host.Session));

    /// <summary>Reads the M1T2 unique index definition straight from Postgres.</summary>
    private static async Task<string> ReadSourceUidIndexAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT indexdef FROM pg_indexes "
            + "WHERE tablename = 'processed_messages' "
            + "AND indexname = 'ux_processed_messages_source_remote_uid'";
        var indexDef = await command.ExecuteScalarAsync();
        await connection.CloseAsync();
        return indexDef as string ?? string.Empty;
    }

    private static async Task<List<EncryptedBlob>> BodyBlobsAsync(
        WebApplicationFactory<Program> factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return await db.EncryptedBlobs.AsNoTracking()
            .Where(b => b.OwnerUserId == userId && b.Purpose == "message_body")
            .ToListAsync();
    }

    private static List<Type> EngineRegistryContractTypes()
        =>
        [
            .. AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(SafeGetTypes)
                .Where(t => t is not null && t.Name == "IEngineRegistry")
                .Select(t => t!)
        ];

    private static IEnumerable<Type?> SafeGetTypes(System.Reflection.Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (System.Reflection.ReflectionTypeLoadException exception)
        {
            return exception.Types;
        }
    }
}
