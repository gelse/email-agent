using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmailAgent.Api.Actions;
using EmailAgent.Connectors.Imap;
using EmailAgent.Contracts.ActionHandlers;
using EmailAgent.Core.Audit;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Actions;
using EmailAgent.Infrastructure.Audit;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using EmailAgent.Infrastructure.Plugins;
using EmailAgent.IntegrationTests.Endpoints;
using EmailAgent.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Xunit;

namespace EmailAgent.IntegrationTests.M4EndToEnd;

/// <summary>
/// M4T5: collection is non-parallel so these facts own the shared Postgres
/// container; Greenmail is shared via IClassFixture and purged per fact.
/// </summary>
[CollectionDefinition("M4E2E", DisableParallelization = true)]
public sealed class M4E2ECollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "M4E2E";
}

[Trait("Category", "M4EndToEnd")]
[Collection(M4E2ECollection.Name)]
public sealed class M4EndToEndTests : IClassFixture<GreenMailFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private readonly GreenMailFixture _greenmail;
    private readonly List<Action> _cleanup = new();

    public M4EndToEndTests(PostgresFixture postgres, GreenMailFixture greenmail)
    {
        _postgres = postgres;
        _greenmail = greenmail;
    }

    public async Task InitializeAsync()
    {
        await _greenmail.PurgeMailAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------------------------------------------------------------------
    // Exit criterion 1: dry-run records intent, mailbox empty (no send)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ExitC1_CreateImapReplyAction_DryRun_RecordsIntent_MailboxEmpty()
    {
        await _greenmail.PurgeMailAsync();
        var host = await M4TestHarness.CreateHostAsync(_postgres, _greenmail);
        await using var factory = host.Factory;

        var config = M4TestHarness.GreenmailSmtpConfig(_greenmail);
        var created = await M4TestHarness.CreateActionViaHttpAsync(
            host, "imap-reply", "My Reply", requireConfirmation: true, dryRunDefault: false, config);
        var actionConfigId = created.GetProperty("id").GetGuid();

        var agent = await M4TestHarness.SeedAgentAsync(factory, host.UserId, "imap-reply");
        var (processedId, sourceMessage) = await M4TestHarness.SeedProcessedMessageAsync(factory, host.UserId);

        var result = await M4TestHarness.DispatchAsync(factory, host.UserId, host.DekBytes, agent.Id, processedId,
            [new DispatchToolCall("imap-reply", JsonSerializer.SerializeToElement(new { body = "Hello from dry-run" }))],
            sourceMessage, dryRunOverride: true);

        result.Decisions.Should().ContainSingle();
        var d = result.Decisions[0];
        d.Kind.Should().Be(ActionDecisionKind.DryRun);
        d.ActionConfigId.Should().Be(actionConfigId);
        d.DryRunDescriptionJson.Should().NotBeNullOrWhiteSpace();
        d.DryRunDescriptionJson.Should().NotContain("\"password\"", "password never in dry-run description");
        d.DryRunDescriptionJson.Should().NotContain("\"username\"", "smtp credentials never in dry-run description");

        using (var desc = JsonDocument.Parse(d.DryRunDescriptionJson!))
        {
            desc.RootElement.GetProperty("kind").GetString().Should().Be("email_send");
            desc.RootElement.GetProperty("from").GetString().Should().Be(GreenMailFixture.Email);
            desc.RootElement.GetProperty("to").GetString().Should().Be(sourceMessage.From);
            desc.RootElement.GetProperty("subject").GetString().Should().StartWith("Re:");
            desc.RootElement.GetProperty("in_reply_to").GetString().Should().Be(sourceMessage.MessageId);
            desc.RootElement.GetProperty("body_text").GetString().Should().Be("Hello from dry-run");
            desc.RootElement.GetProperty("smtp").GetProperty("host").GetString().Should().Be(_greenmail.SmtpHost);
        }

        // actions_executed_json merged
        var processed = await M4TestHarness.FindProcessedAsync(factory, processedId);
        processed.ActionsExecutedJson.Should().NotBeNull();
        using (var ae = JsonDocument.Parse(processed.ActionsExecutedJson!))
        {
            var entry = ae.RootElement.EnumerateArray().Single();
            entry.GetProperty("tool_call_index").GetInt32().Should().Be(0);
            entry.GetProperty("type_id").GetString().Should().Be("imap-reply");
            entry.GetProperty("decision").GetString().Should().Be("dry_run");
            entry.GetProperty("dry_run_description").GetProperty("kind").GetString().Should().Be("email_send");
        }

        // SQ-M4-3: no pending_actions row for dry-run
        (await M4TestHarness.CountPendingAsync(factory, processedId)).Should().Be(0);

        // audit
        var audit = await M4TestHarness.FindAuditAsync(factory, AuditActions.ActionDispatched, processedId);
        audit.Should().ContainSingle(a => JsonDocument.Parse(a.MetadataJson!).RootElement.GetProperty("decision").GetString() == "dry_run");

        // mailbox empty
        (await _greenmail.CountMessagesAsync(GreenMailFixture.Email)).Should().Be(0);
    }

    // ---------------------------------------------------------------------
    // Exit criterion 2: invalid args → audit + user_notification
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ExitC2_InvalidArgs_ProducesInvalidArgsAudit_And_UserNotification()
    {
        var host = await M4TestHarness.CreateHostAsync(_postgres, _greenmail);
        await using var factory = host.Factory;

        await M4TestHarness.CreateActionViaHttpAsync(
            host, "imap-reply", "Reply", requireConfirmation: true, dryRunDefault: false,
            M4TestHarness.GreenmailSmtpConfig(_greenmail));
        var agent = await M4TestHarness.SeedAgentAsync(factory, host.UserId, "imap-reply");
        var (processedId, sourceMessage) = await M4TestHarness.SeedProcessedMessageAsync(factory, host.UserId);

        // body: 42 fails imap-reply ArgsSchema (body must be string, minLength 1)
        var badArgs = JsonSerializer.SerializeToElement(new { body = 42 });
        var result = await M4TestHarness.DispatchAsync(factory, host.UserId, host.DekBytes, agent.Id, processedId,
            [new DispatchToolCall("imap-reply", badArgs)], sourceMessage, dryRunOverride: false);

        result.Decisions.Should().ContainSingle();
        result.Decisions[0].Kind.Should().Be(ActionDecisionKind.InvalidArgs);

        var processed = await M4TestHarness.FindProcessedAsync(factory, processedId);
        using (var ae = JsonDocument.Parse(processed.ActionsExecutedJson!))
        {
            ae.RootElement.EnumerateArray().Single().GetProperty("decision").GetString().Should().Be("invalid_args");
        }

        var invalidAudit = await M4TestHarness.FindAuditAsync(factory, AuditActions.ActionInvalidArgs, processedId);
        invalidAudit.Should().NotBeEmpty();
        using (var meta = JsonDocument.Parse(invalidAudit[0].MetadataJson!))
        {
            meta.RootElement.GetProperty("decision").GetString().Should().Be("invalid_args");
            meta.RootElement.GetProperty("issues").GetArrayLength().Should().BeGreaterThan(0);
        }

        var notifications = await M4TestHarness.FindNotificationsAsync(factory, host.UserId, processedId);
        notifications.Should().ContainSingle(n => n.Kind == "action_invalid_args" && n.RelatedType == "processed_message");
    }

    [Fact]
    public async Task InvalidArgs_NotificationVisibleViaApi()
    {
        var host = await M4TestHarness.CreateHostAsync(_postgres, _greenmail);
        await using var factory = host.Factory;

        await M4TestHarness.CreateActionViaHttpAsync(
            host, "imap-reply", "Reply", requireConfirmation: true, dryRunDefault: false,
            M4TestHarness.GreenmailSmtpConfig(_greenmail));
        var agent = await M4TestHarness.SeedAgentAsync(factory, host.UserId, "imap-reply");
        var (processedId, sourceMessage) = await M4TestHarness.SeedProcessedMessageAsync(factory, host.UserId);

        await M4TestHarness.DispatchAsync(factory, host.UserId, host.DekBytes, agent.Id, processedId,
            [new DispatchToolCall("imap-reply", JsonSerializer.SerializeToElement(new { body = 42 }))],
            sourceMessage);

        using var list = await host.Client.SendAsync(
            SourcesTestHost.Request(HttpMethod.Get, "/v1/user/notifications?filter[unread]=true", host.Bearer));
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await list.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        items.Should().Contain(i => i.GetProperty("kind").GetString() == "action_invalid_args"
            && i.GetProperty("related_id").GetGuid() == processedId);
        var notifId = items.First(i => i.GetProperty("related_id").GetGuid() == processedId).GetProperty("id").GetGuid();

        using var read = await host.Client.SendAsync(
            SourcesTestHost.Request(HttpMethod.Post, $"/v1/user/notifications/{notifId}/read", host.Bearer));
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        using var readDoc = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        readDoc.RootElement.GetProperty("read_at").ValueKind.Should().NotBe(JsonValueKind.Null);

        // cleared from unread filter
        using var list2 = await host.Client.SendAsync(
            SourcesTestHost.Request(HttpMethod.Get, "/v1/user/notifications?filter[unread]=true", host.Bearer));
        using var doc2 = JsonDocument.Parse(await list2.Content.ReadAsStringAsync());
        doc2.RootElement.GetProperty("items").EnumerateArray()
            .Should().NotContain(i => i.GetProperty("id").GetGuid() == notifId);
    }

    // ---------------------------------------------------------------------
    // Real send lands in Greenmail with threading headers
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Execute_SendsMail_LandsInGreenmailMailbox_WithThreadingHeaders()
    {
        await _greenmail.PurgeMailAsync();
        var host = await M4TestHarness.CreateHostAsync(_postgres, _greenmail);
        await using var factory = host.Factory;

        await M4TestHarness.CreateActionViaHttpAsync(
            host, "imap-reply", "Reply Send", requireConfirmation: false, dryRunDefault: false,
            M4TestHarness.GreenmailSmtpConfig(_greenmail));
        var agent = await M4TestHarness.SeedAgentAsync(factory, host.UserId, "imap-reply");
        var (processedId, sourceMessage) = await M4TestHarness.SeedProcessedMessageAsync(factory, host.UserId,
            from: GreenMailFixture.Email, messageId: "<orig-123@greenmail.test>", subject: "Original Subject");

        var result = await M4TestHarness.DispatchAsync(factory, host.UserId, host.DekBytes, agent.Id, processedId,
            [new DispatchToolCall("imap-reply", JsonSerializer.SerializeToElement(new { body = "Reply body here" }))],
            sourceMessage, dryRunOverride: false);

        result.Decisions.Should().ContainSingle(d => d.Kind == ActionDecisionKind.Executed);

        // Poll for delivery and fetch via IMAP (reliable; Greenmail REST list shape varies).
        MimeMessage? mime = null;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            mime = await M4TestHarness.TryFetchFirstMessageViaImapAsync(_greenmail);
            if (mime is not null) break;
            await Task.Delay(250);
        }

        mime.Should().NotBeNull("exactly one message delivered");
        mime!.From.Mailboxes.Single().Address.Should().Be(GreenMailFixture.Email);
        mime.To.Mailboxes.Should().Contain(m => m.Address == GreenMailFixture.Email);
        mime.Subject.Should().Be("Re: Original Subject");
        // MimeKit normalizes Message-Id tokens (angle brackets may vary in representation).
        (mime.InReplyTo ?? string.Empty).Should().Contain("orig-123@greenmail.test");
        var text = mime.TextBody ?? mime.HtmlBody ?? string.Empty;
        text.Should().Contain("Reply body here");
    }

    // ---------------------------------------------------------------------
    // CRUD-writer/dispatcher-reader round-trip
    // ---------------------------------------------------------------------

    [Fact]
    public async Task CrudWriter_And_DispatcherReader_RoundTrip()
    {
        var host = await M4TestHarness.CreateHostAsync(_postgres, _greenmail);
        await using var factory = host.Factory;

        var config = M4TestHarness.GreenmailSmtpConfig(_greenmail);
        var created = await M4TestHarness.CreateActionViaHttpAsync(
            host, "imap-reply", "RoundTrip", requireConfirmation: false, dryRunDefault: true, config);
        var actionId = created.GetProperty("id").GetGuid();

        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IActionConfigProvider>();
        var envelope = await provider.ResolveAsync(host.UserId, "imap-reply", host.DekBytes, CancellationToken.None);
        envelope.Should().NotBeNull();
        envelope!.Id.Should().Be(actionId);
        envelope.Config.GetProperty("host").GetString().Should().Be(_greenmail.SmtpHost);
        envelope.Config.GetProperty("port").GetInt32().Should().Be(_greenmail.SmtpPort);
        envelope.Config.GetProperty("username").GetString().Should().Be(GreenMailFixture.Username);
        envelope.Config.GetProperty("password").GetString().Should().Be(GreenMailFixture.Password);
        envelope.Config.GetProperty("from_address").GetString().Should().Be(GreenMailFixture.Email);
    }

    // ---------------------------------------------------------------------
    // Actions CRUD HTTP round-trip, no password, 409 guard
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ActionsCrud_RoundTrip_NoPassword_CascadeBlob()
    {
        var host = await M4TestHarness.CreateHostAsync(_postgres, _greenmail);
        await using var factory = host.Factory;

        var config = M4TestHarness.GreenmailSmtpConfig(_greenmail);
        var created = await M4TestHarness.CreateActionViaHttpAsync(
            host, "imap-reply", "CRUD Test", requireConfirmation: true, dryRunDefault: false, config);
        var id = created.GetProperty("id").GetGuid();
        created.TryGetProperty("password", out _).Should().BeFalse();
        created.GetProperty("config").TryGetProperty("password", out _).Should().BeFalse("password never returned");

        // GET requires DEK
        using var get = await host.Client.SendAsync(
            SourcesTestHost.Request(HttpMethod.Get, $"/v1/user/actions/{id}", host.Bearer, dek: host.Dek));
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var getBody = await AccountTestHost.ReadJsonAsync(get);
        getBody.GetProperty("config").TryGetProperty("password", out _).Should().BeFalse();

        // PATCH with blank password preserves stored password
        var patchBody = new
        {
            display_name = "CRUD Renamed",
            config = new
            {
                host = _greenmail.SmtpHost,
                port = (int)_greenmail.SmtpPort,
                use_ssl = false,
                username = GreenMailFixture.Username,
                password = "",
                from_address = GreenMailFixture.Email,
                from_name = "Tester",
            },
        };
        using var patch = await host.Client.SendAsync(
            SourcesTestHost.Request(HttpMethod.Patch, $"/v1/user/actions/{id}", host.Bearer, patchBody, host.Dek));
        patch.StatusCode.Should().Be(HttpStatusCode.OK, await patch.Content.ReadAsStringAsync());
        var patched = await AccountTestHost.ReadJsonAsync(patch);
        patched.GetProperty("display_name").GetString().Should().Be("CRUD Renamed");

        // verify password preserved via reader
        using (var scope = factory.Services.CreateScope())
        {
            var provider = scope.ServiceProvider.GetRequiredService<IActionConfigProvider>();
            var env = await provider.ResolveAsync(host.UserId, "imap-reply", host.DekBytes, CancellationToken.None);
            env!.Config.GetProperty("password").GetString().Should().Be(GreenMailFixture.Password);
        }

        var row = (await M4TestHarness.FindActionConfigAsync(factory, id))!;
        var blobId = row.ConfigBlobId;

        // DELETE 409 when pending_actions reference it
        var (_, processedId, _) = await M4TestHarness.SeedAgentAndProcessedAsync(factory, host.UserId);
        await M4TestHarness.SeedPendingActionAsync(factory, processedId, id);
        using var del409 = await host.Client.SendAsync(
            SourcesTestHost.Request(HttpMethod.Delete, $"/v1/user/actions/{id}", host.Bearer));
        del409.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var prob = await AccountTestHost.ReadProblemAsync(del409, HttpStatusCode.Conflict);
        prob.GetProperty("type").GetString().Should().Be("/errors/action-in-use");

        // remove pending ref, then delete succeeds and blob gone
        await M4TestHarness.ClearPendingAsync(factory, id);
        using var del = await host.Client.SendAsync(
            SourcesTestHost.Request(HttpMethod.Delete, $"/v1/user/actions/{id}", host.Bearer));
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await M4TestHarness.FindActionConfigAsync(factory, id, allowNull: true)).Should().BeNull();
        (await M4TestHarness.FindBlobAsync(factory, blobId)).Should().BeNull();
    }

    // ---------------------------------------------------------------------
    // Notifications mark-read (idempotent) and mark-all-read
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Notifications_MarkRead_AndReadAll()
    {
        var host = await M4TestHarness.CreateHostAsync(_postgres, _greenmail);
        await using var factory = host.Factory;

        var n1 = await M4TestHarness.SeedNotificationAsync(factory, host.UserId, "a");
        var n2 = await M4TestHarness.SeedNotificationAsync(factory, host.UserId, "b");

        using var r1 = await host.Client.SendAsync(
            SourcesTestHost.Request(HttpMethod.Post, $"/v1/user/notifications/{n1}/read", host.Bearer));
        r1.StatusCode.Should().Be(HttpStatusCode.OK);
        var r1Body = await AccountTestHost.ReadJsonAsync(r1);
        var readAt1 = r1Body.GetProperty("read_at").GetDateTimeOffset();

        // idempotent: read_at unchanged (timestamptz is microsecond-precision)
        using var r1b = await host.Client.SendAsync(
            SourcesTestHost.Request(HttpMethod.Post, $"/v1/user/notifications/{n1}/read", host.Bearer));
        r1b.StatusCode.Should().Be(HttpStatusCode.OK);
        var r1bBody = await AccountTestHost.ReadJsonAsync(r1b);
        r1bBody.GetProperty("read_at").GetDateTimeOffset().Should().BeCloseTo(readAt1, TimeSpan.FromMilliseconds(1));

        using var all = await host.Client.SendAsync(
            SourcesTestHost.Request(HttpMethod.Post, "/v1/user/notifications/read-all", host.Bearer));
        all.StatusCode.Should().Be(HttpStatusCode.OK);
        var allBody = await AccountTestHost.ReadJsonAsync(all);
        allBody.GetProperty("updated").GetInt32().Should().BeGreaterThanOrEqualTo(1, "at least n2 was unread");

        // second read-all returns 0
        using var all2 = await host.Client.SendAsync(
            SourcesTestHost.Request(HttpMethod.Post, "/v1/user/notifications/read-all", host.Bearer));
        (await AccountTestHost.ReadJsonAsync(all2)).GetProperty("updated").GetInt32().Should().Be(0);
    }

    // ---------------------------------------------------------------------
    // Whitelist rejection: recorded, no notification (Q-AC1 vs Q-AC2)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task WhitelistRejection_Recorded_NoNotification()
    {
        var host = await M4TestHarness.CreateHostAsync(_postgres, _greenmail);
        await using var factory = host.Factory;

        // Agent allows imap-reply, but we dispatch imap-forward
        var agent = await M4TestHarness.SeedAgentAsync(factory, host.UserId, "imap-reply");
        var (processedId, sourceMessage) = await M4TestHarness.SeedProcessedMessageAsync(factory, host.UserId);

        var before = await M4TestHarness.CountNotificationsAsync(factory, host.UserId);
        var result = await M4TestHarness.DispatchAsync(factory, host.UserId, host.DekBytes, agent.Id, processedId,
            [new DispatchToolCall("imap-forward", JsonSerializer.SerializeToElement(new { to = "x@y.com" }))],
            sourceMessage);

        result.Decisions.Should().ContainSingle(d => d.Kind == ActionDecisionKind.RejectedNotAllowed);
        var audit = await M4TestHarness.FindAuditAsync(factory, AuditActions.ActionDispatched, processedId);
        audit.Should().Contain(a => JsonDocument.Parse(a.MetadataJson!).RootElement.GetProperty("decision").GetString() == "rejected_not_allowed");
        (await M4TestHarness.CountNotificationsAsync(factory, host.UserId)).Should().Be(before);
    }

    // ---------------------------------------------------------------------
    // AbortOnFailure both ways (Q-AC3)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task MultiAction_AbortOnFailure_BothWays()
    {
        await _greenmail.PurgeMailAsync();
        var host = await M4TestHarness.CreateHostAsync(_postgres, _greenmail);
        await using var factory = host.Factory;

        await M4TestHarness.CreateActionViaHttpAsync(
            host, "imap-reply", "Reply", requireConfirmation: false, dryRunDefault: false,
            M4TestHarness.GreenmailSmtpConfig(_greenmail));
        await M4TestHarness.CreateActionViaHttpAsync(
            host, "imap-forward", "Fwd", requireConfirmation: false, dryRunDefault: false,
            M4TestHarness.GreenmailSmtpConfig(_greenmail));

        var source = new SourceMessageSnapshot("uid-1", "<m1@test>", "Subj", GreenMailFixture.Email, null, null, null, "hi");
        DispatchToolCall[] calls =
        [
            new("imap-reply", JsonSerializer.SerializeToElement(new { body = "ok1" })),
            new("imap-forward", JsonSerializer.SerializeToElement(new { to = "not-an-email", include_original_body = true })), // invalid → InvalidArgs
            new("imap-reply", JsonSerializer.SerializeToElement(new { body = "ok3" })),
        ];

        // abort_on_action_failure=false: all three recorded
        var (agentCont, pm1, _) = await M4TestHarness.SeedAgentAndProcessedAsync(factory, host.UserId, abortOnFailure: false,
            allowed: ["imap-reply", "imap-forward"]);
        var r1 = await M4TestHarness.DispatchAsync(factory, host.UserId, host.DekBytes, agentCont.Id, pm1, calls, source);
        r1.Decisions.Should().HaveCount(3);
        r1.Decisions[0].Kind.Should().Be(ActionDecisionKind.Executed);
        r1.Decisions[1].Kind.Should().Be(ActionDecisionKind.InvalidArgs);
        r1.Decisions[2].Kind.Should().Be(ActionDecisionKind.Executed);

        // abort_on_action_failure=true: fail on second → third SkippedAbort
        await _greenmail.PurgeMailAsync();
        var (agentAbort, pm2, _) = await M4TestHarness.SeedAgentAndProcessedAsync(factory, host.UserId, abortOnFailure: true,
            allowed: ["imap-reply", "imap-forward"]);
        var r2 = await M4TestHarness.DispatchAsync(factory, host.UserId, host.DekBytes, agentAbort.Id, pm2, calls, source);
        r2.Decisions.Should().HaveCount(3);
        r2.Decisions[0].Kind.Should().Be(ActionDecisionKind.Executed);
        r2.Decisions[1].Kind.Should().Be(ActionDecisionKind.InvalidArgs);
        r2.Decisions[2].Kind.Should().Be(ActionDecisionKind.SkippedAbort);
    }

    // ---------------------------------------------------------------------
    // Audit chain valid with 5 M4 actions (in-process, AuditHmacChain)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task AuditChain_AfterM4Flow_StillValid()
    {
        var host = await M4TestHarness.CreateHostAsync(_postgres, _greenmail);
        await using var factory = host.Factory;

        // Exercise CRUD (create/update/delete) + dispatch (dispatched + invalid_args)
        var config = M4TestHarness.GreenmailSmtpConfig(_greenmail);
        var created = await M4TestHarness.CreateActionViaHttpAsync(
            host, "imap-reply", "Chain Test", requireConfirmation: false, dryRunDefault: true, config);
        var id = created.GetProperty("id").GetGuid();

        using (var patch = await host.Client.SendAsync(SourcesTestHost.Request(HttpMethod.Patch,
            $"/v1/user/actions/{id}", host.Bearer, new { display_name = "Chain Renamed" }, host.Dek)))
        {
            patch.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var agent = await M4TestHarness.SeedAgentAsync(factory, host.UserId, "imap-reply");
        var (processedId, sourceMessage) = await M4TestHarness.SeedProcessedMessageAsync(factory, host.UserId);
        // dry-run dispatch → action_dispatched
        await M4TestHarness.DispatchAsync(factory, host.UserId, host.DekBytes, agent.Id, processedId,
            [new DispatchToolCall("imap-reply", JsonSerializer.SerializeToElement(new { body = "hi" }))],
            sourceMessage, dryRunOverride: true);
        // invalid args → action_invalid_args
        var (processedId2, sourceMessage2) = await M4TestHarness.SeedProcessedMessageAsync(factory, host.UserId, remoteUid: $"uid-{Guid.NewGuid():N}");
        await M4TestHarness.DispatchAsync(factory, host.UserId, host.DekBytes, agent.Id, processedId2,
            [new DispatchToolCall("imap-reply", JsonSerializer.SerializeToElement(new { body = 42 }))],
            sourceMessage2);

        using (var del = await host.Client.SendAsync(
            SourcesTestHost.Request(HttpMethod.Delete, $"/v1/user/actions/{id}", host.Bearer)))
        {
            del.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        // In-process chain verification (mirrors tools/AuditVerify)
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var rows = await db.AuditLog.AsNoTracking().OrderBy(r => r.Id).ToListAsync();
        rows.Should().NotBeEmpty();

        // Same key derivation as AuditLogger/AuditVerify: != 32 UTF-8 bytes → SHA-256.
        var rawKey = Encoding.UTF8.GetBytes("m1t4-integration-test-audit-hmac-key-0123456789");
        var key = rawKey.Length == 32 ? rawKey : SHA256.HashData(rawKey);
        var prev = AuditHmacChain.ComputeGenesis(key);
        foreach (var row in rows)
        {
            var payload = new AuditRowPayload
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
            var expected = AuditHmacChain.ComputeRow(key, prev, payload);
            row.Hmac.Should().Equal(expected, $"audit chain must hold at row id={row.Id} action={row.Action}");
            prev = row.Hmac;
        }

        var actions = rows.Select(r => r.Action).ToHashSet(StringComparer.Ordinal);
        actions.Should().Contain(AuditActions.CreateAction);
        actions.Should().Contain(AuditActions.UpdateAction);
        actions.Should().Contain(AuditActions.DeleteAction);
        actions.Should().Contain(AuditActions.ActionDispatched);
        actions.Should().Contain(AuditActions.ActionInvalidArgs);
    }
}

internal static class M4TestHarness
{
    public sealed record M4Host(
        WebApplicationFactory<Program> Factory,
        HttpClient Client,
        Guid UserId,
        string Bearer,
        string Dek,
        byte[] DekBytes);

    public static async Task<M4Host> CreateHostAsync(PostgresFixture postgres, GreenMailFixture greenmail)
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        var factory = ApiTestFactory.Create(connectionString);
        var client = AccountTestHost.CreateClient(factory);

        // Register IMAP action handlers (plugins dir is empty in tests).
        var registry = factory.Services.GetRequiredService<IPluginRegistry>();
        registry.RegisterActionHandler("imap-reply", new ImapReplyActionHandler());
        registry.RegisterActionHandler("imap-forward", new ImapForwardActionHandler());

        var (userId, bearer, dek) = await SourcesTestHost.AddUserAsync(factory);
        return new M4Host(factory, client, userId, bearer, dek, Convert.FromBase64String(dek));
    }

    public static object GreenmailSmtpConfig(GreenMailFixture gm) => new
    {
        host = gm.SmtpHost,
        port = (int)gm.SmtpPort,
        use_ssl = false,
        username = GreenMailFixture.Username,
        password = GreenMailFixture.Password,
        from_address = GreenMailFixture.Email,
        from_name = "Test Sender",
    };

    public static async Task<JsonElement> CreateActionViaHttpAsync(
        M4Host host, string typeId, string displayName, bool requireConfirmation, bool dryRunDefault, object config)
    {
        var body = new
        {
            type_id = typeId,
            display_name = displayName,
            require_confirmation = requireConfirmation,
            dry_run_default = dryRunDefault,
            config,
        };
        using var response = await host.Client.SendAsync(
            SourcesTestHost.Request(HttpMethod.Post, "/v1/user/actions", host.Bearer, body, host.Dek));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await AccountTestHost.ReadJsonAsync(response);
    }

    public static async Task<AgentConfig> SeedAgentAsync(
        WebApplicationFactory<Program> factory, Guid userId, params string[] allowedTypes)
        => (await SeedAgentAndProcessedAsync(factory, userId, abortOnFailure: false, allowed: allowedTypes)).Agent;

    public static async Task<(AgentConfig Agent, Guid ProcessedId, SourceMessageSnapshot Source)> SeedAgentAndProcessedAsync(
        WebApplicationFactory<Program> factory, Guid userId, bool abortOnFailure = false, string[]? allowed = null,
        string? remoteUid = null)
    {
        allowed ??= ["imap-reply"];
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();

        // Minimal source (FK for processed_messages) + model (FK for agent_configs)
        var source = new MessageSource
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TypeId = "imap",
            DisplayName = $"src-{Guid.NewGuid():N}"[..16],
            ConfigBlobId = Guid.NewGuid(), // not read by these tests
            CreatedAt = DateTimeOffset.UtcNow,
        };
        // config blob placeholder (satisfies FK if enforced; create a real row)
        db.EncryptedBlobs.Add(new EncryptedBlob
        {
            Id = source.ConfigBlobId,
            OwnerUserId = userId,
            Ciphertext = [1, 2, 3],
            Nonce = new byte[12],
            Purpose = "message_source_config",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.MessageSources.Add(source);

        var model = new ModelCatalogEntry
        {
            Id = Guid.NewGuid(),
            DisplayName = "m4-test-model",
            Provider = "openai-compatible",
            BaseUrl = "http://127.0.0.1:1",
            ModelName = "gpt-test",
            Enabled = true,
            CreatedBy = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            ToolCallingProbeResult = ToolCallingProbeResult.Pass,
        };
        db.ModelCatalog.Add(model);

        var agent = new AgentConfig
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            SourceId = source.Id,
            ModelId = model.Id,
            PrefilterJson = "[]",
            PrefilterCombiner = "OR",
            AllowedActionTypeIds = allowed,
            AbortOnActionFailure = abortOnFailure,
            Enabled = true,
        };
        db.AgentConfigs.Add(agent);

        var (processedId, snap) = MakeProcessed(source.Id, remoteUid);
        db.ProcessedMessages.Add(new ProcessedMessage
        {
            Id = processedId,
            SourceId = source.Id,
            RemoteUid = snap.RemoteUid,
            Stage = ProcessedMessageStage.fetched,
            PrefilterMatched = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
        return (agent, processedId, snap);
    }

    public static async Task<(Guid ProcessedId, SourceMessageSnapshot Source)> SeedProcessedMessageAsync(
        WebApplicationFactory<Program> factory, Guid userId, string? from = null, string? messageId = null,
        string? subject = null, string? remoteUid = null)
    {
        // Reuse agent seed path's source by creating a standalone processed message tied to a fresh source
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var source = new MessageSource
        {
            Id = Guid.NewGuid(), UserId = userId, TypeId = "imap", DisplayName = $"s-{Guid.NewGuid():N}"[..12],
            ConfigBlobId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow,
        };
        db.EncryptedBlobs.Add(new EncryptedBlob
        {
            Id = source.ConfigBlobId, OwnerUserId = userId, Ciphertext = [1], Nonce = new byte[12],
            Purpose = "message_source_config", CreatedAt = DateTimeOffset.UtcNow,
        });
        db.MessageSources.Add(source);
        var (pid, snap) = MakeProcessed(source.Id, remoteUid, from, messageId, subject);
        db.ProcessedMessages.Add(new ProcessedMessage
        {
            Id = pid, SourceId = source.Id, RemoteUid = snap.RemoteUid,
            Stage = ProcessedMessageStage.fetched, PrefilterMatched = true, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (pid, snap);
    }

    private static (Guid Id, SourceMessageSnapshot Snap) MakeProcessed(
        Guid sourceId, string? remoteUid = null, string? from = null, string? messageId = null, string? subject = null)
    {
        var id = Guid.NewGuid();
        var snap = new SourceMessageSnapshot(
            remoteUid ?? $"uid-{Guid.NewGuid():N}",
            messageId ?? "<orig@example.test>",
            subject ?? "Test subject",
            from ?? "sender@example.test",
            null, null, null, "Original body text");
        return (id, snap);
    }

    public static async Task<DispatchResult> DispatchAsync(
        WebApplicationFactory<Program> factory, Guid userId, byte[] dek, Guid agentConfigId, Guid processedMessageId,
        IReadOnlyList<DispatchToolCall> calls, SourceMessageSnapshot source, bool? dryRunOverride = null)
    {
        using var scope = factory.Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IActionDispatcher>();
        var request = new DispatchRequest(userId, agentConfigId, processedMessageId, calls, source, dek, dryRunOverride);
        return await dispatcher.DispatchAsync(request, CancellationToken.None);
    }

    public static async Task<ProcessedMessage> FindProcessedAsync(WebApplicationFactory<Program> factory, Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return (await db.ProcessedMessages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id))!;
    }

    public static async Task<int> CountPendingAsync(WebApplicationFactory<Program> factory, Guid processedMessageId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return await db.PendingActions.CountAsync(p => p.ProcessedMessageId == processedMessageId);
    }

    public static async Task SeedPendingActionAsync(WebApplicationFactory<Program> factory, Guid processedMessageId, Guid actionConfigId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        db.PendingActions.Add(new PendingAction
        {
            Id = Guid.NewGuid(), ProcessedMessageId = processedMessageId, ActionConfigId = actionConfigId,
            ArgsJson = "{}", Status = PendingActionStatus.Pending, RequestedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7), CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public static async Task ClearPendingAsync(WebApplicationFactory<Program> factory, Guid actionConfigId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var rows = await db.PendingActions.Where(p => p.ActionConfigId == actionConfigId).ToListAsync();
        db.PendingActions.RemoveRange(rows);
        await db.SaveChangesAsync();
    }

    public static async Task<List<AuditLog>> FindAuditAsync(WebApplicationFactory<Program> factory, string action, Guid processedMessageId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var needle = processedMessageId.ToString();
        var rows = await db.AuditLog.AsNoTracking().Where(a => a.Action == action).OrderBy(a => a.Id).ToListAsync();
        return rows.Where(r => (r.MetadataJson ?? "").Contains(needle, StringComparison.Ordinal)).ToList();
    }

    public static async Task<List<UserNotification>> FindNotificationsAsync(WebApplicationFactory<Program> factory, Guid userId, Guid relatedId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return await db.UserNotifications.AsNoTracking()
            .Where(n => n.UserId == userId && n.RelatedId == relatedId).ToListAsync();
    }

    public static async Task<int> CountNotificationsAsync(WebApplicationFactory<Program> factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return await db.UserNotifications.CountAsync(n => n.UserId == userId);
    }

    public static async Task<Guid> SeedNotificationAsync(WebApplicationFactory<Program> factory, Guid userId, string title)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var id = Guid.NewGuid();
        db.UserNotifications.Add(new UserNotification
        {
            Id = id, UserId = userId, Kind = "info", Title = title, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    public static async Task<ActionConfig?> FindActionConfigAsync(WebApplicationFactory<Program> factory, Guid id, bool allowNull = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var row = await db.ActionConfigs.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id);
        if (!allowNull && row is null) throw new InvalidOperationException($"action config {id} not found");
        return row;
    }

    public static async Task<EncryptedBlob?> FindBlobAsync(WebApplicationFactory<Program> factory, Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return await db.EncryptedBlobs.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id);
    }

    public static MimeMessage ParseGreenmailMessage(JsonElement msg)
    {
        // Greenmail 2.x REST returns objects that may include "mimeMessage", "rawMessage", or content fields.
        // Try common shapes; fall back to constructing from fields.
        if (msg.TryGetProperty("mimeMessage", out var mime) && mime.ValueKind == JsonValueKind.String)
            return MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(mime.GetString()!)));
        if (msg.TryGetProperty("rawMessage", out var raw) && raw.ValueKind == JsonValueKind.String)
            return MimeMessage.Load(new MemoryStream(Convert.FromBase64String(raw.GetString()!)));
        if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            return MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(content.GetString()!)));

        // Construct from fields
        var mm = new MimeMessage();
        if (msg.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.String)
            mm.From.Add(MailboxAddress.Parse(from.GetString()!));
        if (msg.TryGetProperty("to", out var to))
        {
            if (to.ValueKind == JsonValueKind.String) mm.To.Add(MailboxAddress.Parse(to.GetString()!));
            else if (to.ValueKind == JsonValueKind.Array)
                foreach (var t in to.EnumerateArray()) mm.To.Add(MailboxAddress.Parse(t.GetString()!));
        }
        if (msg.TryGetProperty("subject", out var subj)) mm.Subject = subj.GetString() ?? string.Empty;
        if (msg.TryGetProperty("messageId", out var mid)) mm.MessageId = mid.GetString()?.Trim('<', '>') ?? string.Empty;
        if (msg.TryGetProperty("text", out var text)) mm.Body = new TextPart("plain") { Text = text.GetString() ?? "" };
        return mm;
    }

    public static async Task<MimeMessage?> TryFetchFirstMessageViaImapAsync(GreenMailFixture gm)
    {
        try
        {
            using var client = new MailKit.Net.Imap.ImapClient();
            await client.ConnectAsync(gm.ImapHost, gm.ImapPort, MailKit.Security.SecureSocketOptions.None);
            await client.AuthenticateAsync(GreenMailFixture.Username, GreenMailFixture.Password);
            var inbox = client.Inbox;
            await inbox.OpenAsync(MailKit.FolderAccess.ReadOnly);
            if (inbox.Count < 1)
            {
                await client.DisconnectAsync(true);
                return null;
            }

            var message = await inbox.GetMessageAsync(0);
            await client.DisconnectAsync(true);
            return message;
        }
        catch
        {
            return null;
        }
    }
}
