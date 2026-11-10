using System.Text.Json;
using EmailAgent.Api.Sources;
using EmailAgent.Contracts;
using EmailAgent.Contracts.AgentEngines;
using EmailAgent.Contracts.MessageSources;
using EmailAgent.Contracts.Schema;
using EmailAgent.Core.Audit;
using EmailAgent.Core.Security;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Plugins;
using FluentAssertions;
using Json.Schema;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmailAgent.UnitTests.Sources;

/// <summary>
/// M2T5 unit coverage for the <c>SourceService</c> outcomes that need no host
/// and no database: plugin resolution (the task's step-10
/// <c>SourceService_Create rejects unknown plugin</c>), and the test-connection
/// failure taxonomy — expected failures stay <c>Ok=false</c> values, while an
/// unexpected plugin exception becomes the task's documented
/// <c>/errors/source-connection-failed</c> failure instead of escaping raw.
/// </summary>
/// <remarks>
/// Every test here stops before the first SQL statement (plugin resolution and
/// schema validation run first), so the InMemory-backed context is never asked
/// for data — the suite stays offline per the AGENTS.md test constraint.
/// </remarks>
public class SourceServiceTests
{
    [Fact]
    public async Task SourceService_Create_RejectsUnknownPlugin()
    {
        var service = CreateService(new StubRegistry(), out _);

        var create = () => service.CreateAsync(
            Actor(), ValidDek(), "no-such-plugin", "broken source", intervalMinutes: null,
            Config(), CancellationToken.None);

        var exception = await create.Should().ThrowAsync<PluginNotRegisteredException>();
        exception.Which.TypeId.Should().Be("no-such-plugin");
    }

    [Fact]
    public async Task SourceService_TestConnection_UnknownPlugin_ThrowsBeforeAnyNetwork()
    {
        var service = CreateService(new StubRegistry(), out _);

        var test = () => service.TestConnectionAsync(
            Actor(), "no-such-plugin", Config(), CancellationToken.None);

        (await test.Should().ThrowAsync<PluginNotRegisteredException>()).Which.TypeId
            .Should().Be("no-such-plugin");
    }

    [Fact]
    public async Task SourceService_TestConnection_UnexpectedPluginFailure_IsWrappedAsSourceConnectionFailed()
    {
        var registry = new StubRegistry();
        registry.RegisterMessageSource("boom", new ThrowingPlugin());
        var service = CreateService(registry, out var audit);

        var test = () => service.TestConnectionAsync(
            Actor(), "boom", Config(), CancellationToken.None);

        var exception = await test.Should().ThrowAsync<SourceConnectionFailedException>();
        exception.Which.TypeId.Should().Be("boom");
        exception.Which.InnerException.Should().BeOfType<InvalidOperationException>(
            "the plugin's original failure must survive for the 500's detail/log");
        audit.Events.Should().BeEmpty(
            "an aborted connection test must not leave an audit row");
    }

    [Fact]
    public async Task SourceService_TestConnection_SchemaViolation_IsRaisedBeforeThePluginRuns()
    {
        // port=99999 fails the IMAP ConfigSchema: the 400 must come from the
        // API boundary, so the plugin's socket-opening validator never runs.
        var registry = new StubRegistry();
        var plugin = new RecordingPlugin();
        registry.RegisterMessageSource("imap", plugin);
        var service = CreateService(registry, out _);

        using var document = JsonDocument.Parse(
            """{"host":"imap.local","port":99999,"use_ssl":true,"username":"u","password":"p","mailbox":"INBOX"}""");
        var test = () => service.TestConnectionAsync(
            Actor(), "imap", document.RootElement.Clone(), CancellationToken.None);

        var exception = await test.Should().ThrowAsync<ConfigValidationException>();
        exception.Which.Issues.Should().Contain(
            issue => issue.Path == "/port" && issue.Reason == "maximum",
            "the handler renders this as errors[].name='port', reason='must be between 1 and 65535'");
        plugin.ValidateCalls.Should().Be(0, "no socket may be opened for a schema-invalid config");
    }

    // -----------------------------------------------------------------------
    // Stubs — no database rows, no sockets, no external services.
    // -----------------------------------------------------------------------

    private static SourceActor Actor()
        => new(Guid.NewGuid(), "User", Ip: null);

    private static byte[] ValidDek() => new byte[32];

    private static JsonElement Config()
    {
        using var document = JsonDocument.Parse("""{"host":"imap.local"}""");
        return document.RootElement.Clone();
    }

    private static SourceService CreateService(IPluginRegistry registry, out RecordingAudit audit)
    {
        var options = new DbContextOptionsBuilder<EmailAgentDbContext>()
            .UseInMemoryDatabase($"m2t5-{Guid.NewGuid():N}")
            .Options;
        var db = new EmailAgentDbContext(options);
        var store = new EncryptedSourceConfigStore(db, new StubEncryptionService());
        audit = new RecordingAudit();
        return new SourceService(db, registry, store, audit);
    }

    private sealed class StubRegistry : IPluginRegistry
    {
        private readonly Dictionary<string, IMessageSource> _sources = new();

        // M3T1: the second registry slot — the stub mirrors PluginRegistry's
        // one-slot-per-plugin, refusal-wins-visible surface exactly.
        private readonly Dictionary<string, IAgentEngine> _engines = new();

        public void RegisterMessageSource(string pluginId, IMessageSource source)
            => _sources[pluginId] = source;

        public void UnregisterMessageSource(string pluginId) => _sources.Remove(pluginId);

        public IMessageSource? GetMessageSource(string pluginId)
            => _sources.GetValueOrDefault(pluginId);

        public void RegisterAgentEngine(string pluginId, IAgentEngine engine)
            => _engines[pluginId] = engine;

        public void UnregisterAgentEngine(string pluginId) => _engines.Remove(pluginId);

        public IAgentEngine? GetAgentEngine(string pluginId)
            => _engines.GetValueOrDefault(pluginId);

        public IReadOnlyCollection<string> MessageSourceIds => _sources.Keys;

        public IReadOnlyCollection<string> AgentEngineIds => _engines.Keys;

        public void Clear()
        {
            _sources.Clear();
            _engines.Clear();
        }
    }

    private sealed class RecordingAudit : IAuditLogger
    {
        public List<AuditEvent> Events { get; } = new();

        public Task LogAsync(AuditEvent evt, CancellationToken ct)
        {
            Events.Add(evt);
            return Task.CompletedTask;
        }

        public Task<long?> VerifyChainAsync(CancellationToken ct)
            => Task.FromResult<long?>(null);
    }

    /// <summary>The M2T2 schema gate with an out-of-range port, plus a socket seam.</summary>
    private sealed class RecordingPlugin : IMessageSource
    {
        public int ValidateCalls { get; private set; }

        public string TypeId => "imap";

        public JsonSchema ConfigSchema => ImapConfigSchema.Build();

        public Task<ValidationResult> ValidateConfigAsync(JsonElement config, CancellationToken ct)
        {
            ValidateCalls++;
            return Task.FromResult(ValidationResult.Success());
        }

        public async IAsyncEnumerable<RemoteMessage> ListNewMessagesAsync(
            JsonElement config, SourceCheckpoint checkpoint,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<SourceCheckpoint> AdvanceCheckpointAsync(
            JsonElement config, SourceCheckpoint checkpoint,
            IReadOnlyList<RemoteMessage> processed, CancellationToken ct)
            => Task.FromResult(checkpoint);
    }

    /// <summary>A plugin that fails the way an unexpected runtime fault would.</summary>
    private sealed class ThrowingPlugin : IMessageSource
    {
        public string TypeId => "boom";

        public JsonSchema ConfigSchema => new JsonSchemaBuilder().Build();

        public Task<ValidationResult> ValidateConfigAsync(JsonElement config, CancellationToken ct)
            => throw new InvalidOperationException("the connector crashed mid-test");

        public async IAsyncEnumerable<RemoteMessage> ListNewMessagesAsync(
            JsonElement config, SourceCheckpoint checkpoint,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<SourceCheckpoint> AdvanceCheckpointAsync(
            JsonElement config, SourceCheckpoint checkpoint,
            IReadOnlyList<RemoteMessage> processed, CancellationToken ct)
            => Task.FromResult(checkpoint);
    }

    /// <summary>
    /// Minimal <see cref="IEncryptionService"/>: the unit paths never encrypt
    /// (they stop at plugin/schema gates), so only the members outside that
    /// path exist to satisfy the interface.
    /// </summary>
    private sealed class StubEncryptionService : IEncryptionService
    {
        public KdfParams GetDefaultKdfParams() => KdfParams.Default();

        public byte[] DeriveKek(Guid userId, string passphrase, byte[] salt, KdfParams parameters)
            => throw new NotSupportedException();

        public WrappedDek GenerateAndWrapDek(
            Guid userId, string passphrase, byte[] salt, KdfParams parameters)
            => throw new NotSupportedException();

        public byte[] UnwrapDek(
            Guid userId, string passphrase, byte[] salt, KdfParams parameters,
            byte[] dekWrapped, byte[] dekNonce)
            => throw new NotSupportedException();

        public WrappedSecret WrapWithSystemKey(byte[] plaintext) => throw new NotSupportedException();

        public byte[] UnwrapWithSystemKey(byte[] ciphertext, byte[] nonce)
            => throw new NotSupportedException();

        public WrappedSecret WrapWithUserDek(
            Guid userId, byte[] dek, byte[] plaintext, CancellationToken ct = default)
            => throw new NotSupportedException();

        public byte[] UnwrapWithUserDek(
            Guid userId, byte[] dek, byte[] ciphertext, byte[] nonce, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
