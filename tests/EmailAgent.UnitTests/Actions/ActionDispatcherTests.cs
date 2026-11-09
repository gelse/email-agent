using System.Text.Json;
using EmailAgent.Contracts;
using EmailAgent.Contracts.ActionHandlers;
using Json.Schema;
using EmailAgent.Core.Audit;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Actions;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EmailAgent.UnitTests.Actions;

/// <summary>
/// M4T1: dispatcher decision matrix (fakes + EF InMemory). Transaction-
/// atomicity is NOT asserted here (integration tests own it); these facts
/// assert decisions, persistence shapes, and the Q-AC1/AC2/AC3/L6/L7/SQ-M4-3
/// semantics.
/// </summary>
public sealed class ActionDispatcherTests : IDisposable
{
    private readonly TestDispatcherDbContext _db;
    private readonly RecordingAuditLogger _audit = new();
    private readonly FakeHandlerRegistry _handlers = new();
    private readonly FakeConfigProvider _configs = new();
    private readonly IOptions<ActionsOptions> _options =
        Options.Create(new ActionsOptions { PendingConfirmationExpiry = TimeSpan.FromDays(7) });

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _agentConfigId = Guid.NewGuid();
    private readonly Guid _processedMessageId = Guid.NewGuid();
    private readonly Guid _actionConfigId = Guid.NewGuid();

    public ActionDispatcherTests()
    {
        var dbOptions = new DbContextOptionsBuilder<TestDispatcherDbContext>()
            .UseInMemoryDatabase($"m4t1-dispatch-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new TestDispatcherDbContext(dbOptions);
        SeedBase();
    }

    public void Dispose() => _db.Dispose();

    private void SeedBase()
    {
        // A processed message is the dispatch precondition (§10/Q-O9).
        _db.ProcessedMessages.Add(new ProcessedMessage
        {
            Id = _processedMessageId,
            SourceId = Guid.NewGuid(),
            RemoteUid = "uid-1",
            Stage = ProcessedMessageStage.fetched,
            PrefilterMatched = false,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        _db.SaveChanges();
    }

    private void SeedAgentConfig(string[] allowed, bool abortOnFailure = false)
    {
        _db.AgentConfigs.Add(new AgentConfig
        {
            Id = _agentConfigId,
            UserId = _userId,
            SourceId = Guid.NewGuid(),
            ModelId = Guid.NewGuid(),
            AllowedActionTypeIds = allowed,
            AbortOnActionFailure = abortOnFailure,
            Enabled = true,
        });
        _db.SaveChanges();
    }

    private ActionDispatcher CreateDispatcher() => new(
        _db, _handlers, _configs, _audit, _options, NullLogger<ActionDispatcher>.Instance);

    private DispatchRequest Request(params DispatchToolCall[] calls) => new(
        _userId, _agentConfigId, _processedMessageId, calls,
        SourceMessage: null, UserDek: new byte[32]);

    private static DispatchToolCall Call(string name, object args)
        => new(name, JsonSerializer.SerializeToElement(args));

    private void RegisterHandler(string typeId, FakeActionHandler handler) => _handlers.Add(typeId, handler);

    private void RegisterConfig(string typeId, bool requireConfirmation = true, bool dryRunDefault = false)
        => _configs.Add(typeId, new ActionConfigEnvelope(
            _actionConfigId, typeId, "Test Action", requireConfirmation, dryRunDefault,
            JsonSerializer.SerializeToElement(new { host = "smtp", port = 25, use_ssl = false, username = "u", password = "p", from_address = "a@b" })));

    [Fact]
    public async Task NotInWhitelist_RejectedNotAllowed()
    {
        SeedAgentConfig(["other"]);
        RegisterHandler("imap-reply", FakeActionHandler.Ok());
        RegisterConfig("imap-reply");

        var result = await CreateDispatcher().DispatchAsync(Request(Call("imap-reply", new { body = "hi" })), CancellationToken.None);

        result.Decisions.Single().Kind.Should().Be(ActionDecisionKind.RejectedNotAllowed);
        _audit.Events.Should().Contain(e => e.Action == AuditActions.ActionDispatched);
        _db.UserNotifications.Should().BeEmpty();
    }

    [Fact]
    public async Task UnknownHandler_HandlerNotFound()
    {
        SeedAgentConfig(["imap-reply"]);
        RegisterConfig("imap-reply");

        var result = await CreateDispatcher().DispatchAsync(Request(Call("imap-reply", new { body = "hi" })), CancellationToken.None);

        result.Decisions.Single().Kind.Should().Be(ActionDecisionKind.HandlerNotFound);
        _db.UserNotifications.Should().BeEmpty();
    }

    [Fact]
    public async Task NoConfigRow_ConfigNotFound()
    {
        SeedAgentConfig(["imap-reply"]);
        RegisterHandler("imap-reply", FakeActionHandler.Ok());

        var result = await CreateDispatcher().DispatchAsync(Request(Call("imap-reply", new { body = "hi" })), CancellationToken.None);

        result.Decisions.Single().Kind.Should().Be(ActionDecisionKind.ConfigNotFound);
        _db.UserNotifications.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateArgsFails_InvalidArgs_QueuesNotificationAndRecord()
    {
        SeedAgentConfig(["imap-reply"]);
        RegisterHandler("imap-reply", FakeActionHandler.InvalidArgs("body", "required"));
        RegisterConfig("imap-reply", requireConfirmation: false);

        var result = await CreateDispatcher().DispatchAsync(Request(Call("imap-reply", new { })), CancellationToken.None);

        result.Decisions.Single().Kind.Should().Be(ActionDecisionKind.InvalidArgs);
        _db.UserNotifications.Should().ContainSingle(n => n.Kind == "action_invalid_args");
        _db.UserNotifications.Single().RelatedType.Should().Be("processed_message");
        _db.UserNotifications.Single().RelatedId.Should().Be(_processedMessageId);
        _db.ProcessedMessages.Single().ActionsExecutedJson.Should().Contain("\"invalid_args\"");
        _audit.Events.Should().Contain(e => e.Action == AuditActions.ActionInvalidArgs);
        _audit.Events.First(e => e.Action == AuditActions.ActionInvalidArgs).MetadataJson.Should().Contain("\"issues\"");
    }

    [Fact]
    public async Task RequireConfirmationTrue_NoDryRun_PersistsPendingAction_NoExecute()
    {
        SeedAgentConfig(["imap-reply"]);
        var handler = FakeActionHandler.Ok();
        RegisterHandler("imap-reply", handler);
        RegisterConfig("imap-reply", requireConfirmation: true, dryRunDefault: false);

        var result = await CreateDispatcher().DispatchAsync(Request(Call("imap-reply", new { body = "hi" })), CancellationToken.None);

        result.Decisions.Single().Kind.Should().Be(ActionDecisionKind.PendingConfirmation);
        result.Decisions.Single().PendingActionId.Should().NotBeNull();
        handler.ExecuteCount.Should().Be(0);
        _db.PendingActions.Should().ContainSingle();
        var p = _db.PendingActions.Single();
        p.Status.Should().Be(PendingActionStatus.Pending);
        p.ExpiresAt.Should().NotBeNull();
        (p.ExpiresAt!.Value - p.RequestedAt).Should().BeCloseTo(TimeSpan.FromDays(7), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RequireConfirmationTrue_DryRunOverrideTrue_YieldsDryRun_NoPendingRow()
    {
        SeedAgentConfig(["imap-reply"]);
        var handler = FakeActionHandler.DryRunOk();
        RegisterHandler("imap-reply", handler);
        RegisterConfig("imap-reply", requireConfirmation: true, dryRunDefault: false);

        var req = new DispatchRequest(_userId, _agentConfigId, _processedMessageId,
            [Call("imap-reply", new { body = "hi" })], null, new byte[32], DryRunOverride: true);
        var result = await CreateDispatcher().DispatchAsync(req, CancellationToken.None);

        result.Decisions.Single().Kind.Should().Be(ActionDecisionKind.DryRun);
        result.Decisions.Single().DryRunDescriptionJson.Should().NotBeNullOrEmpty();
        _db.PendingActions.Should().BeEmpty();
        handler.LastDryRun.Should().BeTrue();
    }

    [Fact]
    public async Task DryRunDefaultFalse_ExecutesByDefault()
    {
        SeedAgentConfig(["imap-reply"]);
        var handler = FakeActionHandler.Ok();
        RegisterHandler("imap-reply", handler);
        RegisterConfig("imap-reply", requireConfirmation: false, dryRunDefault: false);

        var result = await CreateDispatcher().DispatchAsync(Request(Call("imap-reply", new { body = "hi" })), CancellationToken.None);

        result.Decisions.Single().Kind.Should().Be(ActionDecisionKind.Executed);
        handler.ExecuteCount.Should().Be(1);
        handler.LastDryRun.Should().BeFalse();
    }

    [Fact]
    public async Task DryRunDefaultTrue_DryRunsWithoutOverride()
    {
        SeedAgentConfig(["imap-reply"]);
        var handler = FakeActionHandler.DryRunOk();
        RegisterHandler("imap-reply", handler);
        RegisterConfig("imap-reply", requireConfirmation: false, dryRunDefault: true);

        var result = await CreateDispatcher().DispatchAsync(Request(Call("imap-reply", new { body = "hi" })), CancellationToken.None);

        result.Decisions.Single().Kind.Should().Be(ActionDecisionKind.DryRun);
        handler.LastDryRun.Should().BeTrue();
        _db.PendingActions.Should().BeEmpty();
    }

    [Fact]
    public async Task DryRunOverrideFalse_SuppressesDryRunDefault_Executes_WithConfirmationOff()
    {
        SeedAgentConfig(["imap-reply"]);
        var handler = FakeActionHandler.Ok();
        RegisterHandler("imap-reply", handler);
        RegisterConfig("imap-reply", requireConfirmation: false, dryRunDefault: true);

        var req = new DispatchRequest(_userId, _agentConfigId, _processedMessageId,
            [Call("imap-reply", new { body = "hi" })], null, new byte[32], DryRunOverride: false);
        var result = await CreateDispatcher().DispatchAsync(req, CancellationToken.None);

        result.Decisions.Single().Kind.Should().Be(ActionDecisionKind.Executed);
    }

    [Fact]
    public async Task DryRunOverrideTrue_ForcesDryRun_WhenDefaultFalse()
    {
        SeedAgentConfig(["imap-reply"]);
        var handler = FakeActionHandler.DryRunOk();
        RegisterHandler("imap-reply", handler);
        RegisterConfig("imap-reply", requireConfirmation: false, dryRunDefault: false);

        var req = new DispatchRequest(_userId, _agentConfigId, _processedMessageId,
            [Call("imap-reply", new { body = "hi" })], null, new byte[32], DryRunOverride: true);
        var result = await CreateDispatcher().DispatchAsync(req, CancellationToken.None);

        result.Decisions.Single().Kind.Should().Be(ActionDecisionKind.DryRun);
    }

    [Fact]
    public async Task DryRun_ExecuteFails_DecisionFailed_CountsTowardAbort()
    {
        SeedAgentConfig(["imap-reply", "other"], abortOnFailure: true);
        RegisterHandler("imap-reply", FakeActionHandler.Fail());
        RegisterHandler("other", FakeActionHandler.Ok());
        RegisterConfig("imap-reply", requireConfirmation: false, dryRunDefault: true);
        RegisterConfig("other", requireConfirmation: false, dryRunDefault: false);

        var req = new DispatchRequest(_userId, _agentConfigId, _processedMessageId,
            [Call("imap-reply", new { body = "hi" }), Call("other", new { body = "x" })], null, new byte[32]);
        var result = await CreateDispatcher().DispatchAsync(req, CancellationToken.None);

        result.Decisions[0].Kind.Should().Be(ActionDecisionKind.Failed);
        result.Decisions[1].Kind.Should().Be(ActionDecisionKind.SkippedAbort);
    }

    [Fact]
    public async Task AbortOnFailureTrue_StopsRemaining_AsSkippedAbort()
    {
        SeedAgentConfig(["a", "b", "c"], abortOnFailure: true);
        RegisterHandler("a", FakeActionHandler.Ok());
        RegisterHandler("b", FakeActionHandler.Fail());
        RegisterHandler("c", FakeActionHandler.Ok());
        RegisterConfig("a", requireConfirmation: false);
        RegisterConfig("b", requireConfirmation: false);
        RegisterConfig("c", requireConfirmation: false);

        var result = await CreateDispatcher().DispatchAsync(
            Request(Call("a", new { }), Call("b", new { }), Call("c", new { })), CancellationToken.None);

        result.Decisions.Select(d => d.Kind).Should().Equal(
            ActionDecisionKind.Executed, ActionDecisionKind.Failed, ActionDecisionKind.SkippedAbort);
    }

    [Fact]
    public async Task AbortOnFailureFalse_ContinuesAndRecords()
    {
        SeedAgentConfig(["a", "b", "c"], abortOnFailure: false);
        RegisterHandler("a", FakeActionHandler.Ok());
        RegisterHandler("b", FakeActionHandler.Fail());
        RegisterHandler("c", FakeActionHandler.Ok());
        RegisterConfig("a", requireConfirmation: false);
        RegisterConfig("b", requireConfirmation: false);
        RegisterConfig("c", requireConfirmation: false);

        var result = await CreateDispatcher().DispatchAsync(
            Request(Call("a", new { }), Call("b", new { }), Call("c", new { })), CancellationToken.None);

        result.Decisions.Select(d => d.Kind).Should().Equal(
            ActionDecisionKind.Executed, ActionDecisionKind.Failed, ActionDecisionKind.Executed);
    }

    [Fact]
    public async Task DeclaredOrderPreserved()
    {
        SeedAgentConfig(["a", "b"], abortOnFailure: false);
        RegisterHandler("a", FakeActionHandler.Ok());
        RegisterHandler("b", FakeActionHandler.Ok());
        RegisterConfig("a", requireConfirmation: false);
        RegisterConfig("b", requireConfirmation: false);

        var result = await CreateDispatcher().DispatchAsync(
            Request(Call("a", new { }), Call("b", new { })), CancellationToken.None);

        result.Decisions[0].ToolCallIndex.Should().Be(0);
        result.Decisions[0].TypeId.Should().Be("a");
        result.Decisions[1].ToolCallIndex.Should().Be(1);
    }

    [Fact]
    public async Task PendingConfirmationDoesNotTriggerAbort()
    {
        SeedAgentConfig(["a", "b"], abortOnFailure: true);
        RegisterHandler("a", FakeActionHandler.Ok());
        RegisterHandler("b", FakeActionHandler.Ok());
        RegisterConfig("a", requireConfirmation: true, dryRunDefault: false);
        RegisterConfig("b", requireConfirmation: false, dryRunDefault: false);

        var result = await CreateDispatcher().DispatchAsync(
            Request(Call("a", new { }), Call("b", new { })), CancellationToken.None);

        result.Decisions.Select(d => d.Kind).Should().Equal(
            ActionDecisionKind.PendingConfirmation, ActionDecisionKind.Executed);
    }

    [Fact]
    public async Task Notification_NotWrittenForWhitelistRejection()
    {
        SeedAgentConfig(["x"]);
        var result = await CreateDispatcher().DispatchAsync(Request(Call("nope", new { })), CancellationToken.None);
        result.Decisions.Single().Kind.Should().Be(ActionDecisionKind.RejectedNotAllowed);
        _db.UserNotifications.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessedMessageMissing_Throws()
    {
        // agent config present but processed message id points nowhere
        SeedAgentConfig(["a"]);
        var req = new DispatchRequest(_userId, _agentConfigId, Guid.NewGuid(), [], null, new byte[32]);
        var act = () => CreateDispatcher().DispatchAsync(req, CancellationToken.None);
        await act.Should().ThrowAsync<ProcessedMessageNotFoundException>();
    }

    [Fact]
    public async Task SourceMessage_PassedThrough_Unmodified()
    {
        SeedAgentConfig(["imap-reply"]);
        var handler = FakeActionHandler.Ok();
        RegisterHandler("imap-reply", handler);
        RegisterConfig("imap-reply", requireConfirmation: false);
        var snap = new SourceMessageSnapshot("1", "<id>", "s", "f", "r", "t", "ref", "body");

        var req = new DispatchRequest(_userId, _agentConfigId, _processedMessageId,
            [Call("imap-reply", new { body = "hi" })], snap, new byte[32]);
        await CreateDispatcher().DispatchAsync(req, CancellationToken.None);

        handler.LastSourceMessage.Should().Be(snap);
    }

    private sealed class FakeHandlerRegistry : IActionHandlerRegistry
    {
        private readonly Dictionary<string, IActionHandler> _map = new(StringComparer.Ordinal);
        public void Add(string typeId, IActionHandler h) => _map[typeId] = h;
        public IActionHandler? GetHandler(string typeId) => _map.GetValueOrDefault(typeId);
    }

    private sealed class FakeConfigProvider : IActionConfigProvider
    {
        private readonly Dictionary<string, ActionConfigEnvelope> _map = new(StringComparer.Ordinal);
        public void Add(string typeId, ActionConfigEnvelope e) => _map[typeId] = e;
        public Task<ActionConfigEnvelope?> ResolveAsync(Guid userId, string typeId, ReadOnlyMemory<byte> userDek, CancellationToken ct)
            => Task.FromResult(_map.GetValueOrDefault(typeId));
    }

    private sealed class RecordingAuditLogger : IAuditLogger
    {
        public List<AuditEvent> Events { get; } = [];
        public Task LogAsync(AuditEvent evt, CancellationToken ct) { Events.Add(evt); return Task.CompletedTask; }
        public Task<long?> VerifyChainAsync(CancellationToken ct) => Task.FromResult<long?>(null);
    }

    private sealed class FakeActionHandler : IActionHandler
    {
        private readonly ValidationResult _validate;
        private readonly ActionResult _execute;
        public int ExecuteCount { get; private set; }
        public bool LastDryRun { get; private set; }
        public SourceMessageSnapshot? LastSourceMessage { get; private set; }

        private FakeActionHandler(ValidationResult validate, ActionResult execute)
        { _validate = validate; _execute = execute; }

        public static FakeActionHandler Ok() => new(ValidationResult.Success(), new ActionResult(true));
        public static FakeActionHandler Fail() => new(ValidationResult.Success(), new ActionResult(false, "boom"));
        public static FakeActionHandler DryRunOk() => new(ValidationResult.Success(),
            new ActionResult(true, null, [new SideEffect(SideEffectKinds.WouldSendEmail, "to@x", "{\"kind\":\"email_send\"}")]));
        public static FakeActionHandler InvalidArgs(string path, string reason) => new(
            ValidationResult.Failure("invalid", [new ValidationIssue(path, reason)]), new ActionResult(false));

        public string TypeId => "fake";
        public JsonSchema ConfigSchema => new JsonSchemaBuilder().Type(SchemaValueType.Object).Build();
        public JsonSchema ArgsSchema => new JsonSchemaBuilder().Type(SchemaValueType.Object).Build();
        public Task<ValidationResult> ValidateConfigAsync(JsonElement config, CancellationToken ct) => Task.FromResult(ValidationResult.Success());
        public Task<ValidationResult> ValidateArgsAsync(JsonElement config, JsonElement args, CancellationToken ct) => Task.FromResult(_validate);
        public Task<ActionResult> ExecuteAsync(JsonElement config, JsonElement args, ActionContext ctx, CancellationToken ct)
        { ExecuteCount++; LastDryRun = ctx.DryRun; LastSourceMessage = ctx.SourceMessage; return Task.FromResult(_execute); }
    }

    /// <summary>
    /// EF Core 10 InMemory rejects unmapped <see cref="JsonDocument"/> properties
    /// (e.g. <c>User.KdfParams</c>) during model validation. The dispatcher tests
    /// only touch action/processed-message/agent-config rows, so a string converter
    /// for any <see cref="JsonDocument"/> property makes the model valid without
    /// affecting the entities under test.
    /// </summary>
    private sealed class TestDispatcherDbContext : EmailAgentDbContext
    {
        public TestDispatcherDbContext(DbContextOptions<TestDispatcherDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    if (property.ClrType == typeof(JsonDocument))
                    {
                        property.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<JsonDocument?, string?>(
                            v => v == null ? null : v.RootElement.GetRawText(),
                            v => v == null ? null : JsonDocument.Parse(v, default)));
                    }
                }
            }
        }
    }
}
