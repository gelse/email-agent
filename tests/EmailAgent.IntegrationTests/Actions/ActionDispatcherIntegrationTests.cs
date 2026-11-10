using System.Text.Json;
using EmailAgent.Contracts;
using EmailAgent.Contracts.ActionHandlers;
using EmailAgent.Core.Audit;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Actions;
using EmailAgent.Infrastructure.Blobs;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using EmailAgent.IntegrationTests;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EmailAgent.IntegrationTests.Actions;

/// <summary>
/// M4T1: integration facts (Testcontainers Postgres, real
/// <c>DecryptedActionConfigProvider</c>, fake handlers/crypto as noted).
/// Transaction semantics are exercised on real Postgres.
/// </summary>
[Collection("Postgres")]
public sealed class ActionDispatcherIntegrationTests(PostgresFixture postgres)
{
    private static readonly SemaphoreSlim MigrationGate = new(1, 1);
    private static bool _migrated;

    private EmailAgentDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<EmailAgentDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;
        return new EmailAgentDbContext(opts);
    }

    private async Task EnsureMigratedAsync()
    {
        if (_migrated) return;
        await MigrationGate.WaitAsync();
        try
        {
            if (_migrated) return;
            await using var db = CreateDb();
            await db.Database.MigrateAsync();
            _migrated = true;
        }
        finally { MigrationGate.Release(); }
    }

    private static User MakeUser(Guid? id = null)
    {
        var uid = id ?? Guid.NewGuid();
        return new User
        {
            Id = uid,
            UserName = $"u-{uid:N}",
            NormalizedUserName = $"U-{uid:N}",
            Email = $"{uid:N}@test.local",
            NormalizedEmail = $"{uid:N}@TEST.LOCAL",
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
            Role = UserRole.User,
            Status = UserStatus.Active,
            MustChangePassword = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    [Fact]
    public async Task ConfigProvider_ResolvesMostRecentConfig_WhenMultipleShareTypeId()
    {
        await EnsureMigratedAsync();
        var user = MakeUser();
        await using (var db = CreateDb()) { db.Users.Add(user); await db.SaveChangesAsync(); }

        var older = await SeedActionConfig(user.Id, "imap-reply", DateTimeOffset.UtcNow.AddMinutes(-5), "old");
        var newer = await SeedActionConfig(user.Id, "imap-reply", DateTimeOffset.UtcNow, "new");

        await using var db2 = CreateDb();
        var provider = new DecryptedActionConfigProvider(db2, new FixedCrypto("""{"host":"h"}"""));
        var result = await provider.ResolveAsync(user.Id, "imap-reply", new byte[32], CancellationToken.None);

        result.Should().NotBeNull();
        result!.Id.Should().Be(newer);
        result.DisplayName.Should().Be("new");
    }

    [Fact]
    public async Task ConfigProvider_ReturnsNull_WhenNoRow()
    {
        await EnsureMigratedAsync();
        await using var db = CreateDb();
        var provider = new DecryptedActionConfigProvider(db, new FixedCrypto("{}"));
        var result = await provider.ResolveAsync(Guid.NewGuid(), "imap-reply", new byte[32], CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task ConfigProvider_RejectsForeignOwnerOrWrongPurpose()
    {
        await EnsureMigratedAsync();
        var owner = MakeUser();
        var stranger = MakeUser();
        await using (var db = CreateDb()) { db.Users.AddRange(owner, stranger); await db.SaveChangesAsync(); }

        var blobId = Guid.NewGuid();
        await using (var db = CreateDb())
        {
            db.EncryptedBlobs.Add(new EncryptedBlob
            {
                Id = blobId, OwnerUserId = owner.Id, Ciphertext = [1, 2, 3], Nonce = [4, 5, 6],
                Purpose = "message_source_config", CreatedAt = DateTimeOffset.UtcNow,
            });
            db.ActionConfigs.Add(new ActionConfig
            {
                Id = Guid.NewGuid(), UserId = owner.Id, TypeId = "imap-reply", DisplayName = "x",
                ConfigBlobId = blobId, RequireConfirmation = true, CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using var db2 = CreateDb();
        var provider = new DecryptedActionConfigProvider(db2, new ThrowingCrypto());
        (await provider.ResolveAsync(owner.Id, "imap-reply", new byte[32], CancellationToken.None)).Should().BeNull();
        (await provider.ResolveAsync(stranger.Id, "imap-reply", new byte[32], CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Confirmation_PersistsPendingAction_WithDefaultExpiry()
    {
        await EnsureMigratedAsync();
        var (dispatcher, userId, agentId, processedId, configId) = await SetupDispatcherAsync(requireConfirmation: true, dryRunDefault: false);

        var result = await dispatcher.DispatchAsync(new DispatchRequest(
            userId, agentId, processedId, [new DispatchToolCall("imap-reply", JsonSerializer.SerializeToElement(new { body = "hi" }))],
            null, new byte[32]), CancellationToken.None);

        result.Decisions.Single().Kind.Should().Be(ActionDecisionKind.PendingConfirmation);
        await using var db = CreateDb();
        var p = await db.PendingActions.SingleAsync(x => x.ProcessedMessageId == processedId);
        p.Status.Should().Be(PendingActionStatus.Pending);
        p.ActionConfigId.Should().Be(configId);
        p.ExpiresAt.Should().NotBeNull();
        (p.ExpiresAt!.Value - p.RequestedAt).TotalDays.Should().BeApproximately(7, 0.01);
    }

    [Fact]
    public async Task Executed_UpdatesActionsExecutedJson()
    {
        await EnsureMigratedAsync();
        var (dispatcher, userId, agentId, processedId, _) = await SetupDispatcherAsync(requireConfirmation: false, dryRunDefault: false);

        await dispatcher.DispatchAsync(new DispatchRequest(
            userId, agentId, processedId, [new DispatchToolCall("imap-reply", JsonSerializer.SerializeToElement(new { body = "hi" }))],
            null, new byte[32]), CancellationToken.None);

        await using var db = CreateDb();
        var pm = await db.ProcessedMessages.FindAsync(processedId);
        pm!.ActionsExecutedJson.Should().Contain("\"executed\"");
    }

    [Fact]
    public async Task InvalidArgs_WritesNotificationAndActionsExecuted_AndAuditRowsAfterCommit()
    {
        await EnsureMigratedAsync();
        var audit = new RecordingAudit();
        var (dispatcher, userId, agentId, processedId, _) = await SetupDispatcherAsync(
            requireConfirmation: false, dryRunDefault: false, handler: FakeHandler.InvalidArgs(), audit: audit);

        await dispatcher.DispatchAsync(new DispatchRequest(
            userId, agentId, processedId, [new DispatchToolCall("imap-reply", JsonSerializer.SerializeToElement(new { }))],
            null, new byte[32]), CancellationToken.None);

        await using var db = CreateDb();
        (await db.UserNotifications.CountAsync(n => n.RelatedId == processedId)).Should().Be(1);
        var pm = await db.ProcessedMessages.FindAsync(processedId);
        pm!.ActionsExecutedJson.Should().Contain("\"invalid_args\"");
        audit.Events.Should().Contain(e => e.Action == AuditActions.ActionInvalidArgs);
    }

    private async Task<Guid> SeedActionConfig(Guid userId, string typeId, DateTimeOffset createdAt, string displayName)
    {
        await using var db = CreateDb();
        var blobId = Guid.NewGuid();
        db.EncryptedBlobs.Add(new EncryptedBlob
        {
            Id = blobId, OwnerUserId = userId, Ciphertext = [1], Nonce = [2],
            Purpose = "action_config", CreatedAt = createdAt,
        });
        var id = Guid.NewGuid();
        db.ActionConfigs.Add(new ActionConfig
        {
            Id = id, UserId = userId, TypeId = typeId, DisplayName = displayName,
            ConfigBlobId = blobId, RequireConfirmation = true, CreatedAt = createdAt,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<(ActionDispatcher dispatcher, Guid userId, Guid agentId, Guid processedId, Guid configId)>
        SetupDispatcherAsync(bool requireConfirmation, bool dryRunDefault, IActionHandler? handler = null, IAuditLogger? audit = null)
    {
        var user = MakeUser();
        var sourceBlobId = Guid.NewGuid();
        var configBlobId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var processedId = Guid.NewGuid();
        var configId = Guid.NewGuid();

        await using (var db = CreateDb())
        {
            db.Users.Add(user);
            db.EncryptedBlobs.AddRange(
                new EncryptedBlob { Id = sourceBlobId, OwnerUserId = user.Id, Ciphertext = [9], Nonce = [8], Purpose = "message_source_config", CreatedAt = DateTimeOffset.UtcNow },
                new EncryptedBlob { Id = configBlobId, OwnerUserId = user.Id, Ciphertext = [1], Nonce = [2], Purpose = "action_config", CreatedAt = DateTimeOffset.UtcNow });
            db.MessageSources.Add(new MessageSource
            {
                Id = sourceId, UserId = user.Id, TypeId = "imap", DisplayName = "s",
                ConfigBlobId = sourceBlobId, IntervalMinutes = 5, CheckpointJson = "{}", Status = MessageSourceStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.ModelCatalog.Add(new ModelCatalogEntry
            {
                Id = modelId, DisplayName = "m", Provider = "openai-compatible",
                BaseUrl = "http://x", ModelName = "gpt", CreatedAt = DateTimeOffset.UtcNow,
            });
            db.ActionConfigs.Add(new ActionConfig
            {
                Id = configId, UserId = user.Id, TypeId = "imap-reply", DisplayName = "a",
                ConfigBlobId = configBlobId, RequireConfirmation = requireConfirmation, DryRunDefault = dryRunDefault,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.ProcessedMessages.Add(new ProcessedMessage
            {
                Id = processedId, SourceId = sourceId, RemoteUid = $"u1-{Guid.NewGuid():N}",
                Stage = ProcessedMessageStage.fetched, CreatedAt = DateTimeOffset.UtcNow,
            });
            db.AgentConfigs.Add(new AgentConfig
            {
                Id = agentId, UserId = user.Id, SourceId = sourceId, ModelId = modelId,
                AllowedActionTypeIds = ["imap-reply"], AbortOnActionFailure = false, Enabled = true,
            });
            await db.SaveChangesAsync();
        }

        var dispatchDb = CreateDb();
        var handlers = new Registry();
        handlers.Add("imap-reply", handler ?? FakeHandler.Ok());
        var configs = new DecryptedActionConfigProvider(dispatchDb, new FixedCrypto("""{"host":"h","port":25,"use_ssl":false,"username":"u","password":"p","from_address":"a@b"}"""));
        var dispatcher = new ActionDispatcher(
            dispatchDb, handlers, configs, audit ?? new RecordingAudit(),
            Options.Create(new ActionsOptions()), NullLogger<ActionDispatcher>.Instance);
        return (dispatcher, user.Id, agentId, processedId, configId);
    }

    private sealed class FixedCrypto(string json) : IUserBlobCrypto
    {
        public JsonElement Decrypt(Guid userId, byte[] ciphertext, byte[] nonce, ReadOnlyMemory<byte> userDek, CancellationToken ct = default)
            => JsonSerializer.SerializeToElement(JsonSerializer.Deserialize<JsonElement>(json));
        public (byte[] Ciphertext, byte[] Nonce) Encrypt(Guid userId, JsonElement value, ReadOnlyMemory<byte> userDek, CancellationToken ct = default)
            => ([1], [2]);
    }

    private sealed class ThrowingCrypto : IUserBlobCrypto
    {
        public JsonElement Decrypt(Guid userId, byte[] ciphertext, byte[] nonce, ReadOnlyMemory<byte> userDek, CancellationToken ct = default)
            => throw new InvalidOperationException("decrypt should not be called");
        public (byte[] Ciphertext, byte[] Nonce) Encrypt(Guid userId, JsonElement value, ReadOnlyMemory<byte> userDek, CancellationToken ct = default)
            => ([1], [2]);
    }

    private sealed class Registry : IActionHandlerRegistry
    {
        private readonly Dictionary<string, IActionHandler> _m = new(StringComparer.Ordinal);
        public void Add(string t, IActionHandler h) => _m[t] = h;
        public IActionHandler? GetHandler(string typeId) => _m.GetValueOrDefault(typeId);
    }

    private sealed class RecordingAudit : IAuditLogger
    {
        public List<AuditEvent> Events { get; } = [];
        public Task LogAsync(AuditEvent evt, CancellationToken ct) { Events.Add(evt); return Task.CompletedTask; }
        public Task<long?> VerifyChainAsync(CancellationToken ct) => Task.FromResult<long?>(null);
    }

    private sealed class FakeHandler : IActionHandler
    {
        private readonly ValidationResult _v;
        private readonly ActionResult _r;
        private FakeHandler(ValidationResult v, ActionResult r) { _v = v; _r = r; }
        public static FakeHandler Ok() => new(ValidationResult.Success(), new ActionResult(true));
        public static FakeHandler InvalidArgs() => new(ValidationResult.Failure("bad", [new ValidationIssue("body", "required")]), new ActionResult(false));
        public string TypeId => "imap-reply";
        public Json.Schema.JsonSchema ConfigSchema => new Json.Schema.JsonSchemaBuilder().Build();
        public Json.Schema.JsonSchema ArgsSchema => new Json.Schema.JsonSchemaBuilder().Build();
        public Task<ValidationResult> ValidateConfigAsync(JsonElement config, CancellationToken ct) => Task.FromResult(ValidationResult.Success());
        public Task<ValidationResult> ValidateArgsAsync(JsonElement config, JsonElement args, CancellationToken ct) => Task.FromResult(_v);
        public Task<ActionResult> ExecuteAsync(JsonElement config, JsonElement args, ActionContext ctx, CancellationToken ct) => Task.FromResult(_r);
    }
}
