using System.Net;
using System.Text;
using EmailAgent.Core.Audit;
using EmailAgent.IntegrationTests.Endpoints;
using EmailAgent.IntegrationTests.Fixtures;
using EmailAgent.Infrastructure.Enums;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmailAgent.IntegrationTests.M2EndToEnd;

/// <summary>
/// M2 end-to-end suite entry point (M2T7): the collection is explicitly
/// non-parallel so these facts own their Postgres container end to end — no
/// other test may interleave rows with the smoke-equivalent flows below.
/// </summary>
[CollectionDefinition("M2E2E", DisableParallelization = true)]
public sealed class M2E2ECollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "M2E2E";
}

/// <summary>
/// M2 exit-criterion facts (acceptance criterion: <c>dotnet test --filter
/// Category=M2EndToEnd</c>), one [Fact] per smoke step of
/// <c>scripts/smoke-m2.sh</c> with the same assertions the bash script pins
/// (task M2T7 §Steps 3). Each fact runs the whole chain: a fresh user + DEK
/// (harness), a real Greenmail mailbox seeded over SMTP, fetch-now through the
/// live IMAP plugin, persistence checks, and (for the last fact) the audit
/// HMAC chain walk <c>tools/AuditVerify</c> performs in the smoke script.
/// </summary>
/// <remarks>
/// Recorded adaptation (M2T7 dispatch): the harness starts at the user + DEK
/// step instead of admin login → OTP-login → change-password → set-passphrase
/// → unwrap-DEK; that login half is covered by <c>M1EndToEndTests</c> and by
/// smoke-m2.sh itself, so repeating it per fact would only multiply runtime.
/// </remarks>
[Trait("Category", "M2EndToEnd")]
[Collection(M2E2ECollection.Name)]
public sealed class M2EndToEndTests(PostgresFixture postgres, GreenMailFixture greenMail)
    : IClassFixture<GreenMailFixture>
{
    private const string SeedSubjectPrefix = "M2E2E message";

    private const string SeedBodyPrefix = "injected via greenmail message";

    /// <summary>
    /// Smoke flow 1, steps 6–10 (+ the milestone's dedupe-keyed-record and
    /// no-LLM rows): three SMTP-seeded messages become three
    /// <c>processed_messages</c> rows keyed by UNIQUE(source_id, remote_uid),
    /// each with an encrypted body blob, and nothing engine-shaped happens.
    /// </summary>
    [Fact]
    public async Task HappyPath_FetchNow_PersistsThreeMessages_WithUniqueConstraint_AndNoLlmCall()
    {
        var (host, sourceId) = await SeedAndCreateSourceAsync();
        await using var factory = host.Factory;
        using var client = host.Client;

        using var response = await FetchAsync(host, sourceId);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await AccountTestHost.ReadJsonAsync(response);
        body.GetProperty("processed").GetInt32().Should().Be(3);
        body.GetProperty("new_processed_messages").GetInt32().Should().Be(3);
        body.GetProperty("checkpoint").GetProperty("last_uid").GetString()
            .Should().NotBeNullOrWhiteSpace("the checkpoint advances to the max UID");

        var rows = await SourcesTestHost.MessagesAsync(factory, sourceId);
        rows.Should().HaveCount(3, "one dedupe-keyed record per new message");
        rows.Select(r => uint.Parse(r.RemoteUid)).Should().OnlyHaveUniqueItems(
            "UNIQUE(source_id, remote_uid) is the constraint the second fetch relies on");

        // Every row carries its message_body blob, owned by this caller, and
        // the ciphertext never holds the seeded plaintext (user-owned DEK).
        rows.Should().OnlyContain(r => r.BodyBlobId != null);
        foreach (var row in rows)
        {
            var blob = await SourcesTestHost.BlobAsync(factory, row.BodyBlobId!.Value);
            blob.Purpose.Should().Be("message_body");
            blob.OwnerUserId.Should().Be(host.UserId);
            Encoding.UTF8.GetString(blob.Ciphertext)
                .Should().NotContain(SeedBodyPrefix, "the body must be encrypted, not stored in clear");
        }

        // Smoke step 10 (no LLM call): the row keeps its §6 initial values and
        // the model catalogue is never touched.
        rows.Should().OnlyContain(r => r.Stage == ProcessedMessageStage.fetched);
        rows.Should().OnlyContain(r => r.ModelDecisionJson == null);
        rows.Should().OnlyContain(r => r.PromptTokens == null);
        (await SourcesTestHost.ModelCatalogCountAsync(factory)).Should().Be(
            0, "fetch-now must not create model_catalog rows (M3 wires the engine)");
    }

    /// <summary>
    /// Smoke flow 1, steps 11–12: the second fetch-now reports 0 new messages
    /// and the row count stays 3 (not 6) — the checkpoint plus
    /// <c>ON CONFLICT DO NOTHING</c> keep the dedupe.
    /// </summary>
    [Fact]
    public async Task HappyPath_SecondFetchNow_DoesNotDuplicate()
    {
        var (host, sourceId) = await SeedAndCreateSourceAsync();
        await using var factory = host.Factory;
        using var client = host.Client;

        using (var first = await FetchAsync(host, sourceId))
        {
            first.StatusCode.Should().Be(HttpStatusCode.OK);
            var firstBody = await AccountTestHost.ReadJsonAsync(first);
            firstBody.GetProperty("new_processed_messages").GetInt32().Should().Be(3);
        }

        using var second = await FetchAsync(host, sourceId);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondBody = await AccountTestHost.ReadJsonAsync(second);
        secondBody.GetProperty("processed").GetInt32().Should().Be(
            0, "the checkpoint advanced past every existing UID");
        secondBody.GetProperty("new_processed_messages").GetInt32().Should().Be(0);

        (await SourcesTestHost.MessagesAsync(factory, sourceId)).Should().HaveCount(
            3, "COUNT(*) must be 3, not 6");
    }

    /// <summary>
    /// Smoke flow 1, step 10 in full: <c>stage='fetched'</c>,
    /// <c>prefilter_matched=false</c> and every engine/LLM column NULL — plus
    /// the E2E half the bash script cannot check: each stored body decrypts
    /// back to the seeded plaintext with the caller's DEK only.
    /// </summary>
    [Fact]
    public async Task HappyPath_ProcessedMessages_HaveCorrectStageAndNullEngineColumns()
    {
        var (host, sourceId) = await SeedAndCreateSourceAsync();
        await using var factory = host.Factory;
        using var client = host.Client;

        using var response = await FetchAsync(host, sourceId);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

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

        // Round-trip every body blob under the caller's DEK: the plaintext the
        // connector fetched is what comes back, and it was never stored in clear.
        List<string> decrypted = [];
        foreach (var row in rows)
        {
            // UnwrapConfigAsync is the generic user-DEK unwrap helper: it reads
            // encrypted_blobs by id and decrypts with UnwrapWithUserDek, which
            // is exactly what a message_body blob needs too.
            decrypted.Add(await SourcesTestHost.UnwrapConfigAsync(
                factory, host.UserId, host.Dek, row.BodyBlobId!.Value));
        }

        for (var i = 1; i <= 3; i++)
        {
            var expected = $"{SeedBodyPrefix} {i}";
            decrypted.Should().Contain(text => text.Contains(expected, StringComparison.Ordinal),
                $"seeded message {i} must survive the fetch → encrypt → decrypt round trip");
        }
    }

    /// <summary>
    /// Smoke flow 1, step 7's checkpoint half: <c>last_uid</c> equals the max
    /// <c>remote_uid</c> of the batch, and both the GET projection and the
    /// <c>message_sources</c> row reflect the advance.
    /// </summary>
    [Fact]
    public async Task HappyPath_CheckpointAdvancesToMaxUid()
    {
        var (host, sourceId) = await SeedAndCreateSourceAsync();
        await using var factory = host.Factory;
        using var client = host.Client;

        using var response = await FetchAsync(host, sourceId);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await AccountTestHost.ReadJsonAsync(response);
        var lastUid = body.GetProperty("checkpoint").GetProperty("last_uid").GetString();

        var rows = await SourcesTestHost.MessagesAsync(factory, sourceId);
        rows.Should().HaveCount(3);
        rows.Select(r => uint.Parse(r.RemoteUid)).Max().ToString().Should().Be(
            lastUid, "AdvanceCheckpointAsync takes the max UID of the batch");

        using var get = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Get, $"/v1/user/sources/{sourceId}", host.Bearer, dek: host.Dek));
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await AccountTestHost.ReadJsonAsync(get);
        dto.GetProperty("checkpoint").GetProperty("last_uid").GetString().Should().Be(lastUid);

        var row = await SourcesTestHost.SourceRowAsync(factory, sourceId);
        row.CheckpointJson.Should().Contain(lastUid!);
        row.LastRunAt.Should().NotBeNull("fetch-now records the run it just performed");
    }

    /// <summary>
    /// Task Tests section, fact: the audit HMAC chain stays intact after this
    /// suite's audited writes — the same walk <c>tools/AuditVerify</c> runs
    /// against the live DB (smoke-m2.sh step 23 asserts the CLI's exit code;
    /// <see cref="IAuditLogger.VerifyChainAsync"/> is its in-process twin and
    /// returns the first bad row id, null = chain valid). Recorded adaptation:
    /// the plan's "invoke the AuditVerify binary" is not runnable from inside
    /// a test process hermetically, so the E2E fact walks the chain through the
    /// logger the CLI's own verifier mirrors (M1 precedent, M1EndToEndTests
    /// step 12), and the smoke script covers the binary end to end.
    /// </summary>
    [Fact]
    public async Task AuditChain_Validates()
    {
        // Exercise audited writes first so this run adds fresh chain data.
        var (host, sourceId) = await SeedAndCreateSourceAsync();
        await using var factory = host.Factory;
        using var client = host.Client;
        using var fetch = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Post, $"/v1/user/sources/{sourceId}/fetch-now",
            host.Bearer, dek: host.Dek));
        fetch.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
        (await audit.VerifyChainAsync(CancellationToken.None)).Should().BeNull(
            "every M2 audit row (create/test/fetch/plugin load) keeps the HMAC chain valid");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Purges Greenmail, seeds exactly three messages (subject + body are both
    /// pinned by the assertions) and creates an alice-backed source for a
    /// fresh user — the smoke's "seed 3 → add IMAP source" pair.
    /// </summary>
    private async Task<(SourcesHost Host, Guid SourceId)> SeedAndCreateSourceAsync()
    {
        await greenMail.PurgeMailAsync();
        for (var i = 1; i <= 3; i++)
        {
            await greenMail.SendAsync($"{SeedSubjectPrefix} {i}", $"{SeedBodyPrefix} {i}");
        }

        var host = await SourcesTestHost.CreateUserAsync(postgres);
        var created = await SourcesTestHost.CreateSourceAsync(
            host, "m2e2e greenmail inbox", greenMail.CreateConfig());
        return (host, SourcesTestHost.IdOf(created));
    }

    private Task<HttpResponseMessage> FetchAsync(SourcesHost host, Guid sourceId, string? dek = null)
        => host.Client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Post, $"/v1/user/sources/{sourceId}/fetch-now",
            host.Bearer, dek: dek ?? host.Dek));
}
