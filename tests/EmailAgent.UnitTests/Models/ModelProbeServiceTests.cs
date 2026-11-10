using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using EmailAgent.Api.Config;
using EmailAgent.Api.Models;
using EmailAgent.Contracts;
using EmailAgent.Contracts.AgentEngines;
using EmailAgent.Connectors.OpenAI;
using EmailAgent.Connectors.OpenAI.Http;
using EmailAgent.Core.Audit;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using EmailAgent.UnitTests.Connectors.OpenAI;
using FluentAssertions;
using Json.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace EmailAgent.UnitTests.Models;

/// <summary>
/// M3T2 unit coverage for probe semantics: cache window (pass / fail / re-run
/// paths), the SQ-M3-3 pass condition, and fail-only audit emission — all
/// against a fake <see cref="IAgentEngine"/> returning canned
/// <see cref="AgentEvent"/> sequences, so no HTTP is involved (task-named
/// <c>ModelProbeService_CacheLogic_WithFakeEngine</c> plus the acceptance
/// criteria's pass/fail/audit variants).
/// </summary>
[Trait("Category", "M3T2")]
public class ModelProbeServiceTests
{
    private const string BaseUrl = "https://unit.example.com/v1";
    private const string ApiKey = "sk-unit-test-probe-key";
    private const string ModelName = "probe-model";

    // -----------------------------------------------------------------------
    // Task-named test: cache pass / fail / re-run paths with a fake engine.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ModelProbeService_CacheLogic_WithFakeEngine()
    {
        var db = CreateDb();
        var audit = new RecordingAudit();
        var engine = new FakeEngine(_ => PassEvents());
        var service = CreateService(engine, audit, db);

        // No row at all → the probe runs (canned events, zero HTTP).
        var fresh = await service.ProbeAsync(Config(BaseUrl, "m-none"), null, CancellationToken.None);
        fresh.Outcome.Should().Be(ProbeOutcome.Pass);
        engine.Calls.Should().Be(1);

        // Recent Pass row, same (base_url, model_name) → served from cache.
        var cachedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        Seed(db, BaseUrl, "m-cached", ToolCallingProbeResult.Pass, cachedAt);
        var cached = await service.ProbeAsync(Config(BaseUrl, "m-cached"), null, CancellationToken.None);
        cached.Outcome.Should().Be(ProbeOutcome.PassFromCache);
        cached.CachedFrom.Should().Be(cachedAt);
        cached.RequestExcerpt.Should().BeEmpty("no request ran");
        engine.Calls.Should().Be(1, "a within-window Pass row must short-circuit the engine");

        // Stale Pass row (2 h old, window 1 h) → cache misses, probe re-runs.
        Seed(db, BaseUrl, "m-stale", ToolCallingProbeResult.Pass, DateTimeOffset.UtcNow.AddHours(-2));
        var stale = await service.ProbeAsync(Config(BaseUrl, "m-stale"), null, CancellationToken.None);
        stale.Outcome.Should().Be(ProbeOutcome.Pass);
        engine.Calls.Should().Be(2, "an expired window must re-probe");

        // A stored Fail never serves cache, however fresh it is.
        Seed(db, BaseUrl, "m-failed", ToolCallingProbeResult.Fail, DateTimeOffset.UtcNow.AddMinutes(-1));
        var afterStoredFail = await service.ProbeAsync(Config(BaseUrl, "m-failed"), null, CancellationToken.None);
        afterStoredFail.Outcome.Should().Be(ProbeOutcome.Pass, "only Pass rows are cache-eligible");
        engine.Calls.Should().Be(3, "a stored Fail must never short-circuit the probe");

        // Same model_name behind a different base_url → different cache key.
        Seed(db, BaseUrl, "m-keyed", ToolCallingProbeResult.Pass, DateTimeOffset.UtcNow.AddMinutes(-30));
        var otherEndpoint = await service.ProbeAsync(
            Config("https://other.example.com/v1", "m-keyed"), null, CancellationToken.None);
        otherEndpoint.Outcome.Should().Be(ProbeOutcome.Pass);
        engine.Calls.Should().Be(4, "changing base_url changes the (base_url, model_name) key");

        audit.Events.Should().BeEmpty("every outcome above passed — pass outcomes emit no probe audit row");
    }

    // -----------------------------------------------------------------------
    // Pass condition (SQ-M3-3) and fail reasons.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ModelProbeService_Pass_EveryToolCallIsPing_ReturnsPassAndEmitsNoAuditRow()
    {
        var db = CreateDb();
        var audit = new RecordingAudit();
        var engine = new FakeEngine(_ => PassEvents());
        var service = CreateService(engine, audit, db);

        var result = await service.ProbeAsync(Config(BaseUrl, ModelName), null, CancellationToken.None);

        result.Outcome.Should().Be(ProbeOutcome.Pass);
        result.Reason.Should().BeNull();
        result.CachedFrom.Should().BeNull();
        result.RequestExcerpt.Should().Be(CanonicalRequestExcerpt(ModelName));
        Encoding.UTF8.GetByteCount(result.ResponseExcerpt).Should().BeLessOrEqualTo(256);

        using var body = JsonDocument.Parse(result.ResponseExcerpt);
        var toolCall = body.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("tool_calls")[0];
        toolCall.GetProperty("function").GetProperty("name").GetString().Should().Be("ping");

        audit.Events.Should().BeEmpty();
        engine.Calls.Should().Be(1);
        var lastRequest = engine.LastRequest;
        lastRequest.Should().NotBeNull("the probe must have invoked the engine once");
        lastRequest!.Prompt.Should().Be("ping");
        lastRequest.Context.EngineConfigJson.Should().NotBeNull();
    }

    [Fact]
    public async Task ModelProbeService_Fail_NoToolCalls_ReturnsFailAndEmitsOneAuditRow()
    {
        var db = CreateDb();
        var audit = new RecordingAudit();
        var engine = new FakeEngine(_ => NoToolCallEvents());
        var service = CreateService(engine, audit, db);

        var result = await service.ProbeAsync(Config(BaseUrl, ModelName), null, CancellationToken.None);

        result.Outcome.Should().Be(ProbeOutcome.Fail);
        result.Reason.Should().Be("no_tool_calls");
        result.RequestExcerpt.Should().Be(CanonicalRequestExcerpt(ModelName));
        Encoding.UTF8.GetByteCount(result.ResponseExcerpt).Should().BeLessOrEqualTo(256);
        result.CachedFrom.Should().BeNull();

        audit.Events.Should().HaveCount(1, "a fail emits exactly one ModelProbeFailed row");
        var evt = audit.Events.Single();
        evt.Action.Should().Be(AuditActions.ModelProbeFailed);
        evt.TargetType.Should().Be("model_catalog");
        evt.TargetId.Should().BeNull("the POST path has no catalog row yet (SQ-M3-11)");
        evt.ActorUserId.Should().BeNull("a probe failure is a system event (§6)");

        using var metadata = JsonDocument.Parse(evt.MetadataJson!);
        metadata.RootElement.GetProperty("base_url").GetString().Should().Be(BaseUrl);
        metadata.RootElement.GetProperty("model_name").GetString().Should().Be(ModelName);
        metadata.RootElement.GetProperty("reason").GetString().Should().Be("no_tool_calls");
        metadata.RootElement.GetProperty("request_excerpt").GetString()
            .Should().Be(CanonicalRequestExcerpt(ModelName));
        metadata.RootElement.GetProperty("response_excerpt").GetString().Should().Be(result.ResponseExcerpt);
        metadata.RootElement.GetProperty("duration_ms").GetInt32().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task ModelProbeService_Fail_UnexpectedToolNames_ReturnsFailWithReason()
    {
        var db = CreateDb();
        var audit = new RecordingAudit();
        var engine = new FakeEngine(_ => MixedToolCallEvents());
        var service = CreateService(engine, audit, db);

        var result = await service.ProbeAsync(Config(BaseUrl, ModelName), null, CancellationToken.None);

        result.Outcome.Should().Be(ProbeOutcome.Fail);
        result.Reason.Should().Be(
            "unexpected_tool_names",
            "a mixed tool_calls array fails SQ-M3-3 — every entry must be named ping");
        audit.Events.Should().HaveCount(1);
    }

    // -----------------------------------------------------------------------
    // Response-excerpt clamp (SQ-M3-3: at most 256 UTF-8 bytes, never split).
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ModelProbeService_Fail_LongResponse_ExcerptClampedTo256Utf8Bytes()
    {
        var db = CreateDb();
        var audit = new RecordingAudit();
        var longText = new string('好', 200); // 600 raw bytes / 1200 escaped chars — far over budget
        var engine = new FakeEngine(_ => [Text(longText), Done(4, 9)]);
        var service = CreateService(engine, audit, db);

        var result = await service.ProbeAsync(Config(BaseUrl, ModelName), null, CancellationToken.None);

        // Prove the clamp actually engaged: the unclamped reconstruction is
        // over budget under EITHER encoder behaviour (raw multi-byte or the
        // Web encoder's \uXXXX escaping of non-ASCII).
        var full = ExpectedResponse(longText);
        Encoding.UTF8.GetByteCount(full).Should().BeGreaterThan(256);
        result.ResponseExcerpt.Should().Be(Utf8Excerpt.Truncate(full, 256));
        Encoding.UTF8.GetByteCount(result.ResponseExcerpt).Should().BeLessOrEqualTo(256);
        result.ResponseExcerpt.Should().NotBe(full, "the excerpt must be a strict prefix of the full reconstruction");
        result.ResponseExcerpt.Length.Should().BeLessThan(full.Length);
        result.ResponseExcerpt.Should().NotContain(
            "�", "Utf8Excerpt must cut at a character boundary, never mid-sequence");

        // The persisted audit row carries the same clamped value.
        result.Outcome.Should().Be(ProbeOutcome.Fail);
        using var metadata = JsonDocument.Parse(audit.Events.Single().MetadataJson!);
        var persisted = metadata.RootElement.GetProperty("response_excerpt").GetString();
        persisted.Should().Be(result.ResponseExcerpt);
        Encoding.UTF8.GetByteCount(persisted!).Should().BeLessOrEqualTo(256);
    }

    // -----------------------------------------------------------------------
    // D6: engine transport/config failures propagate untouched — they are not
    // no_tool_calls, so they emit no probe audit row and no cache write.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ModelProbeService_EngineCallFailure_PropagatesWithoutProbeAuditRow()
    {
        var db = CreateDb();
        var audit = new RecordingAudit();
        var engine = new FakeEngine(_ => throw new EngineCallException(502, "upstream bad gateway"));
        var service = CreateService(engine, audit, db);

        var act = () => service.ProbeAsync(Config(BaseUrl, ModelName), null, CancellationToken.None);

        await act.Should().ThrowAsync<EngineCallException>(
            "D6: a transport failure is not a probe outcome — it bubbles to the M3T3 endpoint");
        audit.Events.Should().BeEmpty("no completed drain ⇒ no model_probe_failed row");
        db.ModelCatalog.Should().BeEmpty("a failed engine call must not write any catalog row");
    }

    // -----------------------------------------------------------------------
    // The probe is a gate, not a record: it never writes model_catalog
    // (M3T3 owns the row write on insert/update).
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ModelProbeService_Probe_NeverWritesModelCatalogRows()
    {
        var db = CreateDb();
        var passAudit = new RecordingAudit();
        var failAudit = new RecordingAudit();

        await CreateService(new FakeEngine(_ => PassEvents()), passAudit, db)
            .ProbeAsync(Config(BaseUrl, "m-gate-pass"), null, CancellationToken.None);
        await CreateService(new FakeEngine(_ => NoToolCallEvents()), failAudit, db)
            .ProbeAsync(Config(BaseUrl, "m-gate-fail"), null, CancellationToken.None);

        failAudit.Events.Should().HaveCount(1, "only the failing probe audits");
        db.ModelCatalog.Should().BeEmpty(
            "cache reads are allowed; inserting/updating rows is M3T3's job (§11)");
    }

    // -----------------------------------------------------------------------
    // Audit-row content: correlated on PATCH, never carrying the key.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ModelProbeService_FailAuditMetadata_CarriesExcerptsButNeverApiKey()
    {
        var db = CreateDb();
        var audit = new RecordingAudit();
        var engine = new FakeEngine(_ => NoToolCallEvents());
        var service = CreateService(engine, audit, db);

        await service.ProbeAsync(Config(BaseUrl, ModelName), null, CancellationToken.None);

        var metadataJson = audit.Events.Single().MetadataJson!;
        metadataJson.Should().NotContain(
            ApiKey, "a future change serializing EngineConfigJson must fail this test");
        metadataJson.Should().NotContain("api_key");

        // The excerpt is a JSON string VALUE (escaped inside metadata_json) —
        // parse it and compare the decoded body byte-for-byte.
        using var metadata = JsonDocument.Parse(metadataJson);
        metadata.RootElement.GetProperty("request_excerpt").GetString().Should().Be(
            CanonicalRequestExcerpt(ModelName),
            "the canonical wire body (without the credential) is the request_excerpt");
    }

    [Fact]
    public async Task ModelProbeService_FailAudit_TargetIdFollowsCorrelationModelId()
    {
        var db = CreateDb();
        var audit = new RecordingAudit();
        var engine = new FakeEngine(_ => NoToolCallEvents());
        var service = CreateService(engine, audit, db);
        var modelId = Guid.NewGuid();

        await service.ProbeAsync(Config(BaseUrl, ModelName), modelId, CancellationToken.None);

        audit.Events.Single().TargetId.Should().Be(
            modelId.ToString(), "the PATCH path correlates the fail row to the catalog row");
    }

    // -----------------------------------------------------------------------
    // ModelOptions binding (SQ-M3-3): section Model, env MODEL__*,
    // default 1 hour.
    // -----------------------------------------------------------------------

    [Fact]
    public void ModelOptions_BindsProbeReprobeWindow_FromModelSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // The env var MODEL__ProbeReprobeWindow maps onto exactly this
                // key (double underscore → path separator) in section "Model".
                ["Model:ProbeReprobeWindow"] = "02:30:00",
            })
            .Build();
        using var provider = BuildOptionsProvider(configuration);

        provider.GetRequiredService<IOptions<ModelOptions>>().Value.ProbeReprobeWindow
            .Should().Be(new TimeSpan(2, 30, 0));
    }

    [Fact]
    public void ModelOptions_BindsProbeReprobeWindow_FromEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable("MODEL__ProbeReprobeWindow", "03:00:00");
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            using var provider = BuildOptionsProvider(configuration);

            provider.GetRequiredService<IOptions<ModelOptions>>().Value.ProbeReprobeWindow
                .Should().Be(new TimeSpan(3, 0, 0), "MODEL__ProbeReprobeWindow must bind via section Model");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MODEL__ProbeReprobeWindow", null);
        }
    }

    [Fact]
    public void ModelOptions_DefaultProbeReprobeWindow_IsOneHour()
    {
        new ModelOptions().ProbeReprobeWindow.Should().Be(new TimeSpan(1, 0, 0));

        // Binding against an absent section keeps the SQ-M3-3 default.
        using var provider = BuildOptionsProvider(
            new ConfigurationBuilder().AddInMemoryCollection([]).Build());
        provider.GetRequiredService<IOptions<ModelOptions>>().Value.ProbeReprobeWindow
            .Should().Be(new TimeSpan(1, 0, 0));
    }

    // -----------------------------------------------------------------------
    // Q-L15 follow-up: the probe's canonical max_tokens: 1 reaches the wire.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ModelProbeService_Probe_TransmitsMaxTokensOne_OnTheWire()
    {
        // The real M3T1 engine behind a stub handler — the probe's concrete-
        // engine dispatch (ProbeCallOptions) must put SQ-M3-3's cost bound on
        // the transmitted body, not just in the audit request_excerpt.
        var db = CreateDb();
        var audit = new RecordingAudit();
        var handler = new CapturingHandler(OpenAIFixtureFiles.Read("probe-pass.json"));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(new OpenAICompatibleAgentEngine(httpClient), audit, db);

        var result = await service.ProbeAsync(Config(BaseUrl, ModelName), null, CancellationToken.None);

        result.Outcome.Should().Be(ProbeOutcome.Pass, "the recorded probe-pass fixture yields tool_calls=[ping]");
        handler.LastRequestBody.Should().NotBeNull("the engine transmitted exactly one request");
        using var wire = JsonDocument.Parse(handler.LastRequestBody!);
        JsonElement root = wire.RootElement;
        root.GetProperty("max_tokens").GetInt32().Should().Be(
            1, "EngineCallOptions.MaxTokens puts SQ-M3-3's canonical cost bound on the wire (Q-L15)");
        root.GetProperty("stream").GetBoolean().Should().BeFalse();
        root.GetProperty("model").GetString().Should().Be(ModelName);
        root.GetProperty("tool_choice").GetString().Should().Be("auto");
        JsonElement message = root.GetProperty("messages")[0];
        message.GetProperty("role").GetString().Should().Be("user");
        message.GetProperty("content").GetString().Should().Be("ping");
        JsonElement function = root.GetProperty("tools")[0].GetProperty("function");
        function.GetProperty("name").GetString().Should().Be("ping");
        function.GetProperty("description").GetString().Should().Be("Probe tool; returns nothing.");

        // The audit request_excerpt keeps recording the canonical SQ-M3-3 body
        // byte-for-byte — the canonical fields above are what was transmitted.
        result.RequestExcerpt.Should().Be(CanonicalRequestExcerpt(ModelName));
    }

    // -----------------------------------------------------------------------
    // Stubs — EF InMemory, a recording audit sink and a canned-event engine.
    // -----------------------------------------------------------------------

    private static ServiceProvider BuildOptionsProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddOptions<ModelOptions>().Bind(configuration.GetSection(ModelOptions.SectionName));
        return services.BuildServiceProvider();
    }

    private static OpenAICompatibleConfig Config(string baseUrl, string modelName)
        => new(baseUrl, ApiKey, modelName);

    private static EmailAgentDbContext CreateDb() => new InMemoryProbeDbContext(
        new DbContextOptionsBuilder<EmailAgentDbContext>()
            .UseInMemoryDatabase($"m3t2-{Guid.NewGuid():N}")
            .Options);

    private static ModelProbeService CreateService(
        IAgentEngine engine, RecordingAudit audit, EmailAgentDbContext db, ModelOptions? modelOptions = null) =>
        new(
            db,
            audit,
            Options.Create(modelOptions ?? new ModelOptions()),
            TimeProvider.System,
            () => engine);

    private static void Seed(
        EmailAgentDbContext db,
        string baseUrl,
        string modelName,
        ToolCallingProbeResult result,
        DateTimeOffset probedAt)
    {
        db.ModelCatalog.Add(
            new ModelCatalogEntry
            {
                Id = Guid.NewGuid(),
                DisplayName = $"seeded {modelName}",
                Provider = "openai-compatible",
                BaseUrl = baseUrl,
                ModelName = modelName,
                Enabled = true,
                CreatedBy = Guid.NewGuid(),
                CreatedAt = DateTimeOffset.UtcNow,
                ToolCallingProbeResult = result,
                ToolCallingProbedAt = probedAt,
            });
        db.SaveChanges();
    }

    /// <summary>The SQ-M3-3 canonical request_excerpt for a model (test-side byte-for-byte expectation).</summary>
    private static string CanonicalRequestExcerpt(string modelName) =>
        "{\"model\":\"" + modelName + "\","
        + "\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}],"
        + "\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"ping\","
        + "\"description\":\"Probe tool; returns nothing.\","
        + "\"parameters\":{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}}}],"
        + "\"tool_choice\":\"auto\",\"max_tokens\":1,\"stream\":false}";

    /// <summary>
    /// The unclamped reconstruction the probe's ResponseExcerpt derives from —
    /// same shape and shared serializer options as ModelProbeService's
    /// reconstruction for a single text chunk with usage (4, 9) and no calls.
    /// </summary>
    private static string ExpectedResponse(string content) => JsonSerializer.Serialize(
        new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content,
                        tool_calls = Array.Empty<object>(),
                    },
                },
            },
            usage = new { prompt_tokens = 4, completion_tokens = 9 },
        },
        OpenAIJsonOptions.Default);

    private static IReadOnlyList<AgentEvent> PassEvents() => [ToolCall("ping"), Done(3, 5)];

    private static IReadOnlyList<AgentEvent> NoToolCallEvents() => [Text("I can't do tool calls"), Done(4, 9)];

    private static IReadOnlyList<AgentEvent> MixedToolCallEvents() => [ToolCall("fetch"), Done(1, 1)];

    private static AgentToolCall ToolCall(string name)
    {
        using var document = JsonDocument.Parse("{}");
        return new AgentToolCall(name, document.RootElement.Clone());
    }

    private static AgentTextChunk Text(string value) => new(value);

    private static AgentDone Done(int promptTokens, int completionTokens) => new(promptTokens, completionTokens);

    /// <summary>A fake engine returning canned events — the probe's unit seam (no HTTP anywhere).</summary>
    private sealed class FakeEngine(Func<AgentRequest, IReadOnlyList<AgentEvent>> events) : IAgentEngine
    {
        public int Calls { get; private set; }

        public AgentRequest? LastRequest { get; private set; }

        public string TypeId => "openai-compatible";

        public JsonSchema ConfigSchema => new JsonSchemaBuilder().Build();

        public Task<ValidationResult> ValidateConfigAsync(JsonElement config, CancellationToken ct)
            => Task.FromResult(ValidationResult.Success());

        public async IAsyncEnumerable<AgentEvent> CompleteAsync(
            AgentRequest req, [EnumeratorCancellation] CancellationToken ct)
        {
            Calls++;
            LastRequest = req;
            await Task.CompletedTask;
            foreach (var evt in events(req))
            {
                yield return evt;
            }
        }
    }

    /// <summary>
    /// Stub HTTP handler that captures the transmitted request body and answers
    /// a recorded fixture — the Q-L15 wire-shape seam (no socket, no provider).
    /// </summary>
    private sealed class CapturingHandler(string responseBody) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>
    /// In-memory seam for the probe's cache logic. EF InMemory cannot map the
    /// Npgsql-native <c>jsonb</c> column behind <see cref="User.KdfParams"/>
    /// (the ONLY <see cref="JsonDocument"/>-typed entity property), and this
    /// suite is the first unit suite to materialize the model on the InMemory
    /// provider (the cache lookup queries <see cref="EmailAgentDbContext.ModelCatalog"/>;
    /// earlier suites — e.g. SourceServiceTests — never build the model). The
    /// production mapping is untouched: only this test seam drops that one
    /// property, which the probe never reads.
    /// </summary>
    private sealed class InMemoryProbeDbContext(DbContextOptions<EmailAgentDbContext> options)
        : EmailAgentDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<User>().Ignore(u => u.KdfParams);
        }
    }

    /// <summary>M1T4 audit sink that records rows in memory (SourceServiceTests pattern).</summary>
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
}
