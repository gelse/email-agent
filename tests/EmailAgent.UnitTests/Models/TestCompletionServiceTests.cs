using System.Collections.Concurrent;
using System.Text.Json;
using EmailAgent.Api.Endpoints;
using EmailAgent.Api.Models;
using EmailAgent.Api.Sources;
using EmailAgent.Connectors.OpenAI;
using EmailAgent.Contracts;
using EmailAgent.Contracts.AgentEngines;
using EmailAgent.Core.Audit;
using EmailAgent.Core.Security;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Agents;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Plugins;
using EmailAgent.Infrastructure.Security;
using FluentAssertions;
using Json.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EmailAgent.UnitTests.Models;

// ---------------------------------------------------------------------------
// M3T6: TestCompletionService against EF InMemory, a REAL PluginRegistry
// holding a fake IAgentEngine and a recording audit sink — so the model gates
// (404/400/400), the sanitized 500 detail (SQ-M3-15), the provider gate
// (SQ-M3-14), the D1 null-token posture and the "api_key is never logged" DoD
// are all provable without HTTP, without a container and without a provider.
// ---------------------------------------------------------------------------

[Trait("Category", "M3T6")]
public class TestCompletionServiceTests
{
    private const string Provider = "openai-compatible";
    private const string BaseUrl = "https://unit.example.com/v1";

    /// <summary>Throwaway unit-test key (never a real credential).</summary>
    private const string ApiKey = "sk-unit-m3t6-never-log-0123456789";

    private const string SystemPrompt = "You are helpful.";
    private const string UserPrompt = "Say hello.";

    // -----------------------------------------------------------------------
    // Task-named test: 200 projection + the SQ-M3-4 audit row.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task TestCompletionService_HappyPath_ReturnsTextAndTokens_AndWritesAuditRow()
    {
        var db = CreateDb();
        var engine = new FakeEngine(_ => new AgentEvent[]
        {
            new AgentTextChunk("hello "),
            new AgentTextChunk("world"),
            Done(5, 2, totalTokens: 7, usage: """{"prompt_tokens":5,"completion_tokens":2,"total_tokens":7}"""),
        });
        var cost = new RecordingCost();
        var audit = new RecordingAudit();
        var service = CreateService(db, engine, cost, audit, out var logger);
        var actor = new ModelActor(Guid.NewGuid(), "Admin", null);
        var modelId = await SeedModelAsync(db, enabled: true, prices: true);

        var response = await service.ExecuteAsync(
            modelId, ValidRequest(), actor, CancellationToken.None);

        response.ResponseText.Should().Be("hello world", "the AgentTextChunk events concatenate in order");
        response.PromptTokens.Should().Be(5);
        response.CompletionTokens.Should().Be(2);
        response.TotalTokens.Should().Be(7, "TokenCounts.EffectiveTotalTokens carries the provider total");
        response.CostUsd.Should().Be(
            0.00012m, "(5/1000)*0.02 + (2/1000)*0.01 = 0.00012 — M3T4's price rule");
        response.ToolCalls.Should().BeNull("the response carried no AgentToolCall event");
        response.DurationMs.Should().BeGreaterThanOrEqualTo(0, "wall-clock duration of this one call");

        engine.Calls.Should().Be(1, "exactly one IAgentEngine.CompleteAsync per test completion");
        engine.LastRequest!.Prompt.Should().Be(
            $"SYSTEM:\n{SystemPrompt}\n\nUSER:\n{UserPrompt}", "SQ-M3-9 prompt markers");
        engine.LastRequest.Context.UserId.Should().Be(actor.UserId);
        engine.LastRequest.Context.EngineConfigJson.Should().NotBeNull(
            "the decrypted key travels in-process via EngineConfigJson (SQ-M3-10)");
        var config = engine.LastRequest.Context.EngineConfigJson!.Value;
        config.GetProperty("base_url").GetString().Should().Be(BaseUrl);
        config.GetProperty("api_key").GetString().Should().Be(ApiKey, "the stored blob decrypts to the seeded key");
        config.GetProperty("model_name").GetString().Should().NotBeNullOrWhiteSpace();

        // The call parameters M3T6 must hand to M3T4: no header surface in M3,
        // the provider usage block carried through AgentDone.ProviderUsage.
        cost.Calls.Should().Be(1);
        cost.LastHeaders.Should().BeNull("SQ-M3-17: providerCostHeaders stays null until M5's EngineCaller");
        cost.LastUsage.Should().NotBeNull("providerUsageBlock is done?.ProviderUsage");
        cost.LastUsage!.Value.GetProperty("total_tokens").GetInt32().Should().Be(7);
        cost.LastTokens.Should().Be(new TokenCounts(5, 2, 7), "TokenCounts.From(done), verbatim (D1)");

        var evt = audit.Events.Should().ContainSingle().Which;
        evt.Action.Should().Be(AuditActions.TestModelCompletion, "Q-O17");
        evt.TargetType.Should().Be("model_catalog");
        evt.TargetId.Should().Be(modelId.ToString());
        evt.ActorUserId.Should().Be(actor.UserId);
        evt.ActorRole.Should().Be("Admin");

        using var metadata = JsonDocument.Parse(evt.MetadataJson!);
        var root = metadata.RootElement;
        root.GetProperty("model_id").GetGuid().Should().Be(modelId);
        root.GetProperty("actor_user_id").GetGuid().Should().Be(actor.UserId);
        root.GetProperty("duration_ms").GetInt64().Should().Be(response.DurationMs);
        root.GetProperty("prompt_tokens").GetInt32().Should().Be(5);
        root.GetProperty("completion_tokens").GetInt32().Should().Be(2);
        root.GetProperty("total_tokens").GetInt32().Should().Be(7);
        root.GetProperty("cost_usd").GetDecimal().Should().Be(0.00012m);
        root.GetProperty("tool_call_count").GetInt32().Should().Be(0);
        root.GetProperty("prompt_sha256").GetString().Should().Be(
            TestCompletionService.ComputePromptHash(SystemPrompt, UserPrompt),
            "the audit row carries the hash of THIS prompt pair (SQ-M3-4)");
        root.GetProperty("model_name").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("base_url").GetString().Should().Be(BaseUrl);

        logger.Entries.Should().NotBeEmpty("the success path logs one credential-free info line");
    }

    // -----------------------------------------------------------------------
    // Task-named test: engine failure → sanitized 500 detail (SQ-M3-15).
    // -----------------------------------------------------------------------

    [Fact]
    public async Task TestCompletionService_EngineCallException_ThrowsTestCompletionFailedException_WithSanitizedDetail()
    {
        var db = CreateDb();
        var providerBody =
            "{\"error\":\"rejected key sk-abc123 for Bearer eyJhbGciOi.secret "
            + new string('x', 400) + "\"}";
        var engine = new FakeEngine(_ => Array.Empty<AgentEvent>())
        {
            ThrowOnCall = new EngineCallException(401, providerBody),
        };
        var audit = new RecordingAudit();
        var service = CreateService(db, engine, new RecordingCost(), audit, out _);
        var modelId = await SeedModelAsync(db);

        var act = () => service.ExecuteAsync(
            modelId, ValidRequest(), new ModelActor(Guid.NewGuid(), "Admin", null), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<TestCompletionFailedException>(
            "an HTTP non-2xx from the provider is §13.12 500 /errors/test-completion-failed");
        var detail = exception.Which.Detail;
        detail.Should().NotContain(
            "sk-abc123", "SQ-M3-15: an sk-… key shape echoed by the provider never leaves the process");
        detail.Should().Contain(
            "sk-REDACTED", "the redaction marker proves the key was scrubbed, not merely cut off");
        detail.Should().NotContain(
            "eyJhbGciOi.secret", "SQ-M3-15: a Bearer … token is redacted too");
        detail.Should().Contain("Bearer REDACTED");
        System.Text.Encoding.UTF8.GetByteCount(detail)
            .Should().BeLessOrEqualTo(256, "the detail is clamped to 256 UTF-8 bytes (M3T2's Utf8Excerpt)");

        audit.Events.Should().BeEmpty(
            "a call that never completed writes no test_model_completion row (SQ-M3-4: COMPLETED calls only)");
        engine.Calls.Should().Be(1);
    }

    // -----------------------------------------------------------------------
    // Task-named test: the prompt itself never reaches the audit row.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task TestCompletionService_AuditRow_DoesNotContainPromptText()
    {
        var db = CreateDb();
        var engine = new FakeEngine(_ => new AgentEvent[]
        {
            new AgentTextChunk("ok"),
            Done(1, 1),
        });
        var audit = new RecordingAudit();
        var service = CreateService(db, engine, new RecordingCost(), audit, out _);
        var modelId = await SeedModelAsync(db);

        await service.ExecuteAsync(
            modelId,
            ValidRequest(
                systemPrompt: "SECRET-SYSTEM-PROMPT-m3t6",
                userPrompt: "SECRET-USER-PROMPT-m3t6"),
            new ModelActor(Guid.NewGuid(), "Admin", null),
            CancellationToken.None);

        var evt = audit.Events.Should().ContainSingle().Which;
        evt.MetadataJson.Should().NotContain(
            "SECRET-SYSTEM-PROMPT-m3t6", "the prompt may hold user-style data — only its hash is audited");
        evt.MetadataJson.Should().NotContain(
            "SECRET-USER-PROMPT-m3t6", "the prompt may hold user-style data — only its hash is audited");
        evt.MetadataJson.Should().NotContain("system_prompt");
        evt.MetadataJson.Should().NotContain("user_prompt");
        evt.MetadataJson.Should().Contain(
            TestCompletionService.ComputePromptHash("SECRET-SYSTEM-PROMPT-m3t6", "SECRET-USER-PROMPT-m3t6"),
            "the hash is what correlates the row back to the prompt without disclosing it");
    }

    // -----------------------------------------------------------------------
    // Task-named test: the disabled gate runs BEFORE any engine call.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task TestCompletionService_DisabledModel_ThrowsModelDisabledException()
    {
        var db = CreateDb();
        var engine = new FakeEngine(_ => new AgentEvent[] { new AgentTextChunk("never"), Done(1, 1) });
        var audit = new RecordingAudit();
        var service = CreateService(db, engine, new RecordingCost(), audit, out _);
        var modelId = await SeedModelAsync(db, enabled: false);

        var act = () => service.ExecuteAsync(
            modelId, ValidRequest(), new ModelActor(Guid.NewGuid(), "Admin", null), CancellationToken.None);

        await act.Should().ThrowAsync<ModelDisabledException>(
            "a disabled model is §13.12 400 /errors/model-disabled, never a socket attempt");
        engine.Calls.Should().Be(0, "the gate runs before the engine is invoked");
        audit.Events.Should().BeEmpty("nothing ran, so nothing is audited");
    }

    /// <summary>SQ-M3-14: a provider with no registered plugin fails CLEARLY, not with a null reference.</summary>
    [Fact]
    public async Task UnknownProvider_ThrowsClearFailure()
    {
        var db = CreateDb();
        var registry = new PluginRegistry(); // registers openai-compatible below — the model's provider is NOT that
        var audit = new RecordingAudit();
        var service = CreateService(
            db,
            new FakeEngine(_ => Array.Empty<AgentEvent>()),
            new RecordingCost(),
            audit,
            out var logger,
            registry: registry);
        var modelId = await SeedModelAsync(db, provider: "acme-llm");

        var act = () => service.ExecuteAsync(
            modelId, ValidRequest(), new ModelActor(Guid.NewGuid(), "Admin", null), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<TestCompletionFailedException>(
            "an unregistered provider must surface as a clear §13.12 failure (SQ-M3-14)");
        exception.Which.Detail.Should().Contain(
            "acme-llm", "the failure names the provider so an admin can act on it");
        exception.Which.Detail.Should().NotBeNullOrWhiteSpace();
        audit.Events.Should().BeEmpty("no completion ran → no audit row");
        logger.Entries.Should().Contain(
            entry => entry.Contains("acme-llm", StringComparison.Ordinal),
            "the refusal is logged with model id + provider (never a credential)");
    }

    /// <summary>M3T4 deviation D1: usage-absent responses keep their nulls — null tokens, NULL cost, never 0/$0.</summary>
    [Fact]
    public async Task UsageAbsent_PreservesNullTokensAndNullCost()
    {
        var db = CreateDb();
        var engine = new FakeEngine(_ => new AgentEvent[]
        {
            new AgentTextChunk("no usage reported"),
            new AgentDone(null, null), // provider reported no usage at all
        });
        var cost = new RecordingCost();
        var audit = new RecordingAudit();

        // The REAL cost service: the null cost must survive the whole stack,
        // not just a stub's say-so.
        var service = CreateService(db, engine, cost, audit, out _, costImplementation: cost);
        var actor = new ModelActor(Guid.NewGuid(), "Admin", null);
        var modelId = await SeedModelAsync(db, prices: true);

        var response = await service.ExecuteAsync(modelId, ValidRequest(), actor, CancellationToken.None);

        response.PromptTokens.Should().BeNull("D1: absent usage is not 0");
        response.CompletionTokens.Should().BeNull("D1: absent usage is not 0");
        response.TotalTokens.Should().Be(0, "total_tokens is the documented int — the sum of nothing is 0");
        response.CostUsd.Should().BeNull("tokens unknown → cost unknown (M3T4 rule 2 skipped), NEVER 0");
        cost.LastTokens.Should().Be(
            new TokenCounts(null, null, null), "TokenCounts.From(done) preserves every null (no `?? 0`)");

        using var metadata = JsonDocument.Parse(audit.Events.Single().MetadataJson!);
        metadata.RootElement.GetProperty("prompt_tokens").ValueKind.Should().Be(JsonValueKind.Null);
        metadata.RootElement.GetProperty("completion_tokens").ValueKind.Should().Be(JsonValueKind.Null);
        metadata.RootElement.GetProperty("cost_usd").ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>DoD: the API key (and the prompt) NEVER reach a log line — scanned across success and failure.</summary>
    [Fact]
    public async Task NeverLogsApiKeyOrPrompt()
    {
        var db = CreateDb();
        var engine = new FakeEngine(_ => new AgentEvent[]
        {
            new AgentTextChunk("ok"),
            Done(1, 1),
        });
        var audit = new RecordingAudit();
        var service = CreateService(db, engine, new RecordingCost(), audit, out var logger);
        var actor = new ModelActor(Guid.NewGuid(), "Admin", null);
        var modelId = await SeedModelAsync(db);

        // Success path …
        await service.ExecuteAsync(
            modelId,
            ValidRequest(
                systemPrompt: "PROMPT-SECRET-SYSTEM-m3t6",
                userPrompt: "PROMPT-SECRET-USER-m3t6"),
            actor,
            CancellationToken.None);

        // … and the failure path (its warning must not carry the provider body either).
        engine.ThrowOnCall = new EngineCallException(
            401, """{"error":"bad key sk-abc123"}""");
        var failedAct = () => service.ExecuteAsync(modelId, ValidRequest(), actor, CancellationToken.None);
        await failedAct.Should().ThrowAsync<TestCompletionFailedException>();

        logger.Entries.Should().NotBeEmpty("both paths logged something to scan");
        foreach (var entry in logger.Entries)
        {
            entry.Should().NotContain(ApiKey, "the decrypted key must never be logged");
            entry.Should().NotContain("sk-abc123", "nor any provider-echoed key shape");
            entry.Should().NotContain("PROMPT-SECRET-SYSTEM-m3t6", "the prompt is user-style data");
            entry.Should().NotContain("PROMPT-SECRET-USER-m3t6", "the prompt is user-style data");
            entry.Should().NotContain("api_key", "no config member names either (task Risks)");
        }
    }

    // -----------------------------------------------------------------------
    // Review fixes: transport failures + unreadable key pointer.
    // -----------------------------------------------------------------------

    /// <summary>
    /// An HttpRequestException (connection refused, DNS, truncated read) never
    /// arrives as EngineCallException — it must still map to the typed 500
    /// /errors/test-completion-failed with a SANITIZED detail, never the
    /// generic /errors/server-error, and the log carries the TYPE only.
    /// </summary>
    [Fact]
    public async Task TransportFailure_ThrowsTestCompletionFailedException_WithSanitizedDetail()
    {
        var db = CreateDb();
        var engine = new FakeEngine(_ => Array.Empty<AgentEvent>())
        {
            ThrowOnCall = new HttpRequestException(
                "Connection refused (10.0.0.9:443) with key sk-transport-leak-987654321 "
                + "and header Bearer eyJhbGciOi.transport-token"),
        };
        var audit = new RecordingAudit();
        var service = CreateService(db, engine, new RecordingCost(), audit, out var logger);
        var modelId = await SeedModelAsync(db);

        var act = () => service.ExecuteAsync(
            modelId, ValidRequest(), new ModelActor(Guid.NewGuid(), "Admin", null), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<TestCompletionFailedException>(
            "a transport failure must not escape to the generic /errors/server-error");
        exception.Which.Detail.Should().NotContain(
            "sk-transport-leak-987654321", "SQ-M3-15 redaction applies to transport messages too");
        exception.Which.Detail.Should().Contain("sk-REDACTED");
        exception.Which.Detail.Should().Contain("Bearer REDACTED");
        audit.Events.Should().BeEmpty("no completion ran → no audit row (SQ-M3-4)");
        engine.Calls.Should().Be(1);
        logger.Entries.Should().Contain(
            entry => entry.Contains("HttpRequestException", StringComparison.Ordinal),
            "the warning records the exception TYPE, never the message");
        logger.Entries.Should().NotContain(
            entry => entry.Contains("sk-transport-leak-987654321", StringComparison.Ordinal),
            "the transport message must never reach a log line");
    }

    /// <summary>
    /// An unreadable api_key_secret_ref (null or not a Guid) is §13.12 400
    /// /errors/config-validation naming api_key — never a socket attempt,
    /// never an audit row (M3T3 PATCH-path posture; review fix coverage).
    /// </summary>
    [Fact]
    public async Task MissingSecretRef_ThrowsConfigValidationException_NamingApiKey()
    {
        var db = CreateDb();
        var engine = new FakeEngine(_ => new AgentEvent[] { Done(1, 1) });
        var audit = new RecordingAudit();
        var service = CreateService(db, engine, new RecordingCost(), audit, out _);
        var modelId = await SeedModelAsync(db);
        var entry = db.ModelCatalog.Single(row => row.Id == modelId);

        entry.ApiKeySecretRef = null;
        await db.SaveChangesAsync();

        var nullAct = () => service.ExecuteAsync(
            modelId, ValidRequest(), new ModelActor(Guid.NewGuid(), "Admin", null), CancellationToken.None);
        var nullException = await nullAct.Should().ThrowAsync<ConfigValidationException>(
            "a null key pointer is the 400 config-validation class");
        nullException.Which.Issues.Should().Contain(issue => issue.Path == "/api_key");

        entry.ApiKeySecretRef = "not-a-guid";
        await db.SaveChangesAsync();

        var malformedAct = () => service.ExecuteAsync(
            modelId, ValidRequest(), new ModelActor(Guid.NewGuid(), "Admin", null), CancellationToken.None);
        var malformedException = await malformedAct.Should().ThrowAsync<ConfigValidationException>(
            "a malformed key pointer is the same 400 class");
        malformedException.Which.Issues.Should().Contain(issue => issue.Path == "/api_key");

        engine.Calls.Should().Be(0, "both gates run before the engine is invoked");
        audit.Events.Should().BeEmpty("a refused test completion writes no audit row");
    }

    // -----------------------------------------------------------------------
    // Stubs.
    // -----------------------------------------------------------------------

    private static TestCompletionRequest ValidRequest(
        string? systemPrompt = SystemPrompt, string? userPrompt = UserPrompt)
        => new()
        {
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
            Tools =
            [
                new ToolSpecDto
                {
                    Name = "get_weather",
                    Description = "Looks up the weather for a city.",
                    Parameters = System.Text.Json.Nodes.JsonNode.Parse(
                        """{"type":"object","properties":{"city":{"type":"string"}}}"""),
                },
            ],
        };

    private static AgentDone Done(
        int promptTokens,
        int completionTokens,
        int? totalTokens = null,
        string? usage = null)
        => new(promptTokens, completionTokens)
        {
            TotalTokens = totalTokens,
            ProviderUsage = usage is null ? null : ParseUsage(usage),
        };

    private static JsonElement ParseUsage(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static TestCompletionService CreateService(
        InMemoryDbContext db,
        FakeEngine engine,
        RecordingCost cost,
        RecordingAudit audit,
        out RecordingLogger<TestCompletionService> logger,
        ICostDerivationService? costImplementation = null,
        IPluginRegistry? registry = null)
    {
        var pluginRegistry = registry ?? new PluginRegistry();
        pluginRegistry.RegisterAgentEngine(Provider, engine);
        logger = new RecordingLogger<TestCompletionService>();
        return new TestCompletionService(
            db,
            new EncryptedModelApiKeyStore(db, CreateEncryption(), TimeProvider.System),
            pluginRegistry,
            costImplementation ?? cost,
            audit,
            logger);
    }

    /// <summary>Seeds a catalog row plus its encrypted key blob (M3T3's persisted state).</summary>
    private static async Task<Guid> SeedModelAsync(
        InMemoryDbContext db,
        bool enabled = true,
        bool prices = false,
        string provider = Provider)
    {
        var id = Guid.NewGuid();
        var blobId = await new EncryptedModelApiKeyStore(db, CreateEncryption(), TimeProvider.System)
            .WriteAsync(ApiKey, CancellationToken.None);

        db.ModelCatalog.Add(
            new ModelCatalogEntry
            {
                Id = id,
                DisplayName = "M3T6 unit model",
                Provider = provider,
                BaseUrl = BaseUrl,
                ModelName = $"m3t6-unit-{Guid.NewGuid():N}",
                Enabled = enabled,
                CreatedBy = Guid.NewGuid(),
                CreatedAt = DateTimeOffset.UtcNow,
                ApiKeySecretRef = blobId.ToString(),
                PricePer1kInputUsd = prices ? 0.02m : null,
                PricePer1kOutputUsd = prices ? 0.01m : null,
            });
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>
    /// InMemory seam (M3T3/M3T2 pattern): drop the jsonb-mapped
    /// <see cref="User.KdfParams"/> property EF InMemory cannot map.
    /// </summary>
    private sealed class InMemoryDbContext(DbContextOptions<EmailAgentDbContext> options)
        : EmailAgentDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<User>().Ignore(u => u.KdfParams);
        }
    }

    /// <summary>A fake engine returning canned events (or throwing) — the no-HTTP unit seam.</summary>
    private sealed class FakeEngine(Func<AgentRequest, IReadOnlyList<AgentEvent>> events) : IAgentEngine
    {
        public int Calls { get; private set; }

        public AgentRequest? LastRequest { get; private set; }

        /// <summary>When set, the call throws instead of yielding events (the engine-failure seam).</summary>
        public Exception? ThrowOnCall { get; set; }

        public string TypeId => Provider;

        public JsonSchema ConfigSchema => new JsonSchemaBuilder().Build();

        public Task<ValidationResult> ValidateConfigAsync(JsonElement config, CancellationToken ct)
            => Task.FromResult(ValidationResult.Success());

        public async IAsyncEnumerable<AgentEvent> CompleteAsync(
            AgentRequest req, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Calls++;
            LastRequest = req;
            await Task.CompletedTask;
            if (ThrowOnCall is not null)
            {
                throw ThrowOnCall;
            }

            foreach (var evt in events(req))
            {
                yield return evt;
            }
        }
    }

    /// <summary>
    /// Delegates to the REAL <see cref="CostDerivationService"/> while
    /// recording the exact call M3T6 made (SQ-M3-17's pass-through contract).
    /// </summary>
    private sealed class RecordingCost : ICostDerivationService
    {
        private readonly CostDerivationService _inner = new();

        public int Calls { get; private set; }

        public TokenCounts? LastTokens { get; private set; }

        public ModelCatalogEntry? LastEntry { get; private set; }

        public IReadOnlyDictionary<string, string>? LastHeaders { get; private set; }

        public JsonElement? LastUsage { get; private set; }

        public decimal? DeriveCost(
            TokenCounts tokens,
            ModelCatalogEntry? modelCatalogEntry,
            IReadOnlyDictionary<string, string>? providerCostHeaders,
            JsonElement? providerUsageBlock)
        {
            Calls++;
            LastTokens = tokens;
            LastEntry = modelCatalogEntry;
            LastHeaders = providerCostHeaders;
            LastUsage = providerUsageBlock;
            return _inner.DeriveCost(tokens, modelCatalogEntry, providerCostHeaders, providerUsageBlock);
        }
    }

    /// <summary>M1T4 audit sink that records rows in memory (M3T3/M3T2 pattern).</summary>
    private sealed class RecordingAudit : IAuditLogger
    {
        public List<AuditEvent> Events { get; } = new();

        public Task LogAsync(AuditEvent evt, CancellationToken ct)
        {
            Events.Add(evt);
            return Task.CompletedTask;
        }

        public Task<long?> VerifyChainAsync(CancellationToken ct) => Task.FromResult<long?>(null);
    }

    /// <summary>Captures every formatted log line (and its raw state) for content assertions.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Enqueue(formatter(state, exception));
            Entries.Enqueue($"{state}");
            if (exception is not null)
            {
                Entries.Enqueue(exception.ToString());
            }
        }
    }

    private static InMemoryDbContext CreateDb() => new(
        new DbContextOptionsBuilder<EmailAgentDbContext>()
            .UseInMemoryDatabase($"m3t6-service-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static IEncryptionService CreateEncryption()
        => new Argon2idAesGcmEncryptionService(
            Options.Create(
                new EncryptionOptions
                {
                    KeyEncryptionKey = "m3t6-unit-service-kek-0123456789abcdef",
                }),
            NullLogger<Argon2idAesGcmEncryptionService>.Instance);
}
