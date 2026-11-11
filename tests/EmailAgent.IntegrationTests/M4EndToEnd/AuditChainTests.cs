using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmailAgent.Contracts.ActionHandlers;
using EmailAgent.Core.Audit;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Actions;
using EmailAgent.Infrastructure.Audit;
using EmailAgent.IntegrationTests.Endpoints;
using EmailAgent.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmailAgent.IntegrationTests.M4EndToEnd;

/// <summary>
/// M4T5: self-contained in-process HMAC chain verification (mirrors
/// <c>tools/AuditVerify</c>) proving the chain remains valid and the five M4
/// audit actions appear after an M4 CRUD + dispatch flow.
/// </summary>
[Trait("Category", "M4EndToEnd")]
[Collection(M4E2ECollection.Name)]
public sealed class AuditChainTests(PostgresFixture postgres)
{
    [Fact]
    public async Task AuditChain_IsValid_AndContainsAllFiveM4Actions()
    {
        // Minimal self-contained M4 flow (no external services needed).
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        var factory = ApiTestFactory.Create(connectionString);
        await using var __ = factory;
        AccountTestHost.CreateClient(factory); // boot host

        var registry = factory.Services.GetRequiredService<Infrastructure.Plugins.IPluginRegistry>();
        registry.RegisterActionHandler("imap-reply", new global::EmailAgent.Connectors.Imap.ImapReplyActionHandler());

        var (userId, bearer, dek, session) = await SourcesTestHost.AddUserAsync(factory);
        var host = new M4TestHarness.M4Host(factory, AccountTestHost.CreateClient(factory), userId, bearer, dek, Convert.FromBase64String(dek), session);

        // Config uses a dummy SMTP endpoint; only schema validation runs on CRUD,
        // and dispatches below are dry-run / invalid-args (no SMTP session).
        var smtpConfig = new
        {
            host = "127.0.0.1",
            port = 2525,
            use_ssl = false,
            username = "u",
            password = "p",
            from_address = "from@example.test",
            from_name = "From",
        };

        // create_action, update_action
        var created = await M4TestHarness.CreateActionViaHttpAsync(
            host, "imap-reply", "Chain", requireConfirmation: false, dryRunDefault: true, smtpConfig);
        var id = created.GetProperty("id").GetGuid();
        using (var patch = await host.Client.SendAsync(SourcesTestHost.Request(HttpMethod.Patch,
            $"/v1/user/actions/{id}", host.Bearer, new { display_name = "Chain2" }, host.Session)))
            patch.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var agent = await M4TestHarness.SeedAgentAsync(factory, host.UserId, "imap-reply");
        var (pm1, src1) = await M4TestHarness.SeedProcessedMessageAsync(factory, host.UserId);
        // action_dispatched (dry-run)
        await M4TestHarness.DispatchAsync(factory, host.UserId, host.DekBytes, agent.Id, pm1,
            [new DispatchToolCall("imap-reply", JsonSerializer.SerializeToElement(new { body = "hi" }))], src1, dryRunOverride: true);
        // action_invalid_args
        var (pm2, src2) = await M4TestHarness.SeedProcessedMessageAsync(factory, host.UserId, remoteUid: $"uid-{Guid.NewGuid():N}");
        await M4TestHarness.DispatchAsync(factory, host.UserId, host.DekBytes, agent.Id, pm2,
            [new DispatchToolCall("imap-reply", JsonSerializer.SerializeToElement(new { body = 42 }))], src2);

        // delete_action
        using (var del = await host.Client.SendAsync(
            SourcesTestHost.Request(HttpMethod.Delete, $"/v1/user/actions/{id}", host.Bearer, session: host.Session)))
            del.StatusCode.Should().Be(System.Net.HttpStatusCode.NoContent);

        // Verify full chain
        await using var db = new EmailAgentDbContext(
            new DbContextOptionsBuilder<EmailAgentDbContext>().UseNpgsql(connectionString).Options);
        var rows = await db.AuditLog.AsNoTracking().OrderBy(r => r.Id).ToListAsync();
        rows.Should().NotBeEmpty();

        var rawKey = Encoding.UTF8.GetBytes("m1t4-integration-test-audit-hmac-key-0123456789");
        var key = rawKey.Length == 32 ? rawKey : SHA256.HashData(rawKey);
        var prev = AuditHmacChain.ComputeGenesis(key);
        foreach (var row in rows)
        {
            var payload = new AuditRowPayload
            {
                Id = row.Id, ActorUserId = row.ActorUserId, ActorRole = row.ActorRole, Action = row.Action,
                TargetType = row.TargetType, TargetId = row.TargetId, MetadataJson = row.MetadataJson,
                Ip = row.Ip?.ToString(), CreatedAt = row.CreatedAt.ToUnixTimeSeconds(),
            };
            var expected = AuditHmacChain.ComputeRow(key, prev, payload);
            row.Hmac.Should().Equal(expected, $"chain must hold at id={row.Id} action={row.Action}");
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
