using System.Text.Json;
using EmailAgent.Api.Actions;
using EmailAgent.Contracts;
using EmailAgent.Contracts.ActionHandlers;
using EmailAgent.Contracts.AgentEngines;
using EmailAgent.Contracts.MessageSources;
using EmailAgent.Core.Audit;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Blobs;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Plugins;
using FluentAssertions;
using Json.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Xunit;

namespace EmailAgent.UnitTests.Actions;

public sealed class ActionConfigServiceTests : IDisposable
{
    private readonly TestDb _db;
    private readonly RecordingAudit _audit = new();
    private readonly StubRegistry _registry = new();
    private readonly ActionConfigService _service;
    private readonly ActionActor _actor;

    public ActionConfigServiceTests()
    {
        var opts = new DbContextOptionsBuilder<TestDb>()
            .UseInMemoryDatabase($"m4t3-actions-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new TestDb(opts);
        var userId = Guid.NewGuid();
        _actor = new ActionActor(userId, "User", null);
        _service = new ActionConfigService(_db, _registry, new EncryptedActionConfigStore(_db, new PassthroughCrypto()), _audit);
        _registry.Register("imap-reply", new TestHandler("imap-reply"));
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Create_UnregisteredPlugin_Throws()
    {
        var act = () => _service.CreateAsync(_actor, new byte[32], "telegram-send", "x", null, null,
            JsonSerializer.SerializeToElement(new { host = "h", username = "u", password = "p", from_address = "a@b" }), CancellationToken.None);
        await act.Should().ThrowAsync<ActionPluginNotRegisteredException>();
    }

    [Fact]
    public async Task Create_SchemaInvalid_ThrowsConfigValidation_NoRows()
    {
        var act = () => _service.CreateAsync(_actor, new byte[32], "imap-reply", "x", null, null,
            JsonSerializer.SerializeToElement(new { host = "h", port = 99999, username = "u", password = "p", from_address = "a@b" }), CancellationToken.None);
        var ex = await act.Should().ThrowAsync<ActionConfigValidationException>();
        ex.Which.Issues.Should().NotBeEmpty();
        _db.ActionConfigs.Should().BeEmpty();
        _db.EncryptedBlobs.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_Valid_PersistsAndAudits_DefaultsApplied()
    {
        var config = JsonSerializer.SerializeToElement(new { host = "smtp.x", username = "u", password = "secret", from_address = "a@b.com" });
        var row = await _service.CreateAsync(_actor, new byte[32], "imap-reply", "My Reply", null, null, config, CancellationToken.None);

        row.Entity.RequireConfirmation.Should().BeTrue();
        row.Entity.DryRunDefault.Should().BeFalse();
        _db.ActionConfigs.Should().ContainSingle();
        _db.EncryptedBlobs.Should().ContainSingle(b => b.Purpose == "action_config");
        _audit.Events.Should().Contain(e => e.Action == AuditActions.CreateAction);
        _audit.Events.First(e => e.Action == AuditActions.CreateAction).MetadataJson.Should().NotContain("secret");
    }

    [Fact]
    public async Task Get_ForeignId_ThrowsNotFound()
    {
        var config = JsonSerializer.SerializeToElement(new { host = "smtp.x", username = "u", password = "p", from_address = "a@b.com" });
        var created = await _service.CreateAsync(_actor, new byte[32], "imap-reply", "x", null, null, config, CancellationToken.None);

        var other = new ActionActor(Guid.NewGuid(), "User", null);
        var act = () => _service.GetAsync(other, new byte[32], created.Entity.Id, CancellationToken.None);
        await act.Should().ThrowAsync<ActionConfigNotFoundException>();
    }

    [Fact]
    public async Task Delete_WithPendingAction_ThrowsActionInUse_NothingDeleted()
    {
        var config = JsonSerializer.SerializeToElement(new { host = "smtp.x", username = "u", password = "p", from_address = "a@b.com" });
        var created = await _service.CreateAsync(_actor, new byte[32], "imap-reply", "x", null, null, config, CancellationToken.None);

        _db.PendingActions.Add(new PendingAction
        {
            Id = Guid.NewGuid(), ProcessedMessageId = Guid.NewGuid(), ActionConfigId = created.Entity.Id,
            ArgsJson = "{}", Status = EmailAgent.Infrastructure.Enums.PendingActionStatus.Pending,
            RequestedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();

        var act = () => _service.DeleteAsync(_actor, created.Entity.Id, CancellationToken.None);
        await act.Should().ThrowAsync<ActionInUseException>();
        _db.ActionConfigs.Should().ContainSingle();
        _db.EncryptedBlobs.Should().ContainSingle();
    }

    [Fact]
    public async Task Delete_NoPending_RemovesRowAndBlob()
    {
        var config = JsonSerializer.SerializeToElement(new { host = "smtp.x", username = "u", password = "p", from_address = "a@b.com" });
        var created = await _service.CreateAsync(_actor, new byte[32], "imap-reply", "x", null, null, config, CancellationToken.None);

        await _service.DeleteAsync(_actor, created.Entity.Id, CancellationToken.None);

        _db.ActionConfigs.Should().BeEmpty();
        _db.EncryptedBlobs.Should().BeEmpty();
        _audit.Events.Should().Contain(e => e.Action == AuditActions.DeleteAction);
    }

    [Fact]
    public async Task Update_PasswordOmitted_PreservesStoredPassword()
    {
        var config = JsonSerializer.SerializeToElement(new { host = "smtp.x", username = "u", password = "secret", from_address = "a@b.com" });
        var created = await _service.CreateAsync(_actor, new byte[32], "imap-reply", "x", null, null, config, CancellationToken.None);

        // PATCH config without password
        var patch = JsonSerializer.SerializeToElement(new { host = "smtp2.x", username = "u", from_address = "a@b.com" });
        var updated = await _service.UpdateAsync(_actor, new byte[32], created.Entity.Id, "renamed", null, null, patch, CancellationToken.None);

        updated.Entity.DisplayName.Should().Be("renamed");
        // stored config (service row) retains password
        updated.Config.GetProperty("password").GetString().Should().Be("secret");
        updated.Config.GetProperty("host").GetString().Should().Be("smtp2.x");
        // DTO strips password
        var dto = EmailAgent.Api.Endpoints.ActionConfigDto.FromRow(updated);
        dto.Config.TryGetProperty("password", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Notifications_MarkRead_Idempotent_And_MarkAllRead()
    {
        var userId = _actor.UserId;
        _db.UserNotifications.AddRange(
            new UserNotification { Id = Guid.NewGuid(), UserId = userId, Kind = "k", Title = "t", CreatedAt = DateTimeOffset.UtcNow },
            new UserNotification { Id = Guid.NewGuid(), UserId = userId, Kind = "k", Title = "t2", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        await _db.SaveChangesAsync();

        var svc = new NotificationService(_db);
        var nActor = new NotificationActor(userId, "User", null);
        var list = await svc.ListAsync(nActor, unreadOnly: true, null, 25, CancellationToken.None);
        list.Items.Should().HaveCount(2);

        var first = await svc.MarkReadAsync(nActor, list.Items[0].Id, CancellationToken.None);
        first.ReadAt.Should().NotBeNull();
        var again = await svc.MarkReadAsync(nActor, list.Items[0].Id, CancellationToken.None);
        again.ReadAt.Should().Be(first.ReadAt); // never overwritten

        var updated = await svc.MarkAllReadAsync(nActor, CancellationToken.None);
        updated.Should().Be(1); // only the still-unread one
    }

    [Fact]
    public async Task Notification_MarkRead_ForeignId_ThrowsNotFound()
    {
        var svc = new NotificationService(_db);
        var act = () => svc.MarkReadAsync(new NotificationActor(Guid.NewGuid(), "User", null), Guid.NewGuid(), CancellationToken.None);
        await act.Should().ThrowAsync<NotificationNotFoundException>();
    }

    private sealed class TestDb(DbContextOptions<TestDb> options) : EmailAgentDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var et in modelBuilder.Model.GetEntityTypes())
                foreach (var p in et.GetProperties())
                    if (p.ClrType == typeof(JsonDocument))
                        p.SetValueConverter(new ValueConverter<JsonDocument?, string?>(
                            v => v == null ? null : v.RootElement.GetRawText(),
                            v => v == null ? null : JsonDocument.Parse(v, default)));
        }
    }

    private sealed class PassthroughCrypto : IUserBlobCrypto
    {
        public JsonElement Decrypt(Guid userId, byte[] ciphertext, byte[] nonce, ReadOnlyMemory<byte> userDek, CancellationToken ct = default)
            => JsonSerializer.Deserialize<JsonElement>(ciphertext);
        public (byte[] Ciphertext, byte[] Nonce) Encrypt(Guid userId, JsonElement value, ReadOnlyMemory<byte> userDek, CancellationToken ct = default)
            => (JsonSerializer.SerializeToUtf8Bytes(value), [1]);
    }

    private sealed class StubRegistry : IPluginRegistry
    {
        private readonly Dictionary<string, IMessageSource> _s = new();
        private readonly Dictionary<string, IAgentEngine> _e = new();
        private readonly Dictionary<string, IActionHandler> _a = new();
        public void Register(string typeId, IActionHandler h) => _a[typeId] = h;
        public void RegisterMessageSource(string pluginId, IMessageSource source) => _s[pluginId] = source;
        public void UnregisterMessageSource(string pluginId) => _s.Remove(pluginId);
        public IMessageSource? GetMessageSource(string pluginId) => _s.GetValueOrDefault(pluginId);
        public void RegisterAgentEngine(string pluginId, IAgentEngine engine) => _e[pluginId] = engine;
        public void UnregisterAgentEngine(string pluginId) => _e.Remove(pluginId);
        public IAgentEngine? GetAgentEngine(string pluginId) => _e.GetValueOrDefault(pluginId);
        public void RegisterActionHandler(string pluginId, IActionHandler handler) => _a[pluginId] = handler;
        public void UnregisterActionHandler(string pluginId) => _a.Remove(pluginId);
        public IActionHandler? GetActionHandler(string pluginId) => _a.GetValueOrDefault(pluginId);
        public IReadOnlyCollection<string> MessageSourceIds => _s.Keys;
        public IReadOnlyCollection<string> AgentEngineIds => _e.Keys;
        public IReadOnlyCollection<string> ActionHandlerIds => _a.Keys;
        public void Clear() { _s.Clear(); _e.Clear(); _a.Clear(); }
    }

    private sealed class RecordingAudit : IAuditLogger
    {
        public List<AuditEvent> Events { get; } = [];
        public Task LogAsync(AuditEvent evt, CancellationToken ct) { Events.Add(evt); return Task.CompletedTask; }
        public Task<long?> VerifyChainAsync(CancellationToken ct) => Task.FromResult<long?>(null);
    }

    private sealed class TestHandler(string typeId) : IActionHandler
    {
        public string TypeId { get; } = typeId;
        public JsonSchema ConfigSchema => EmailAgent.Contracts.Schema.ImapActionConfigSchema.Build();
        public JsonSchema ArgsSchema => new JsonSchemaBuilder().Build();
        public Task<ValidationResult> ValidateConfigAsync(JsonElement config, CancellationToken ct) => Task.FromResult(ValidationResult.Success());
        public Task<ValidationResult> ValidateArgsAsync(JsonElement config, JsonElement args, CancellationToken ct) => Task.FromResult(ValidationResult.Success());
        public Task<ActionResult> ExecuteAsync(JsonElement config, JsonElement args, ActionContext ctx, CancellationToken ct) => Task.FromResult(new ActionResult(true));
    }
}
