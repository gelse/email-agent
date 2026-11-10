using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EmailAgent.Api.Endpoints;
using EmailAgent.Api.Sources;
using EmailAgent.Connectors.OpenAI;
using EmailAgent.Contracts;
using EmailAgent.Contracts.AgentEngines;
using EmailAgent.Contracts.Schema;
using EmailAgent.Core.Audit;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Agents;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Plugins;
using Json.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmailAgent.Api.Models;

/// <summary>
/// M3T6: the model is <c>enabled = false</c> — the endpoint refuses to call a
/// disabled model (task error table → §13.12 400 <c>/errors/model-disabled</c>).
/// </summary>
public sealed class ModelDisabledException : Exception
{
    public ModelDisabledException(Guid modelId)
        : base($"Model {modelId} is disabled — a test completion only runs against enabled models.")
        => ModelId = modelId;

    public Guid ModelId { get; }
}

/// <summary>
/// M3T6: the completion could not run (no engine plugin for the provider —
/// SQ-M3-14 — or the engine call itself failed). <see cref="Exception.Message"/>
/// is the ALREADY-SANITIZED §13.12 <c>detail</c> (SQ-M3-15: <c>sk-…</c> /
/// <c>Bearer …</c> redacted, clamped to 256 UTF-8 bytes), so the handler can
/// surface it verbatim without ever touching the raw provider body.
/// </summary>
public sealed class TestCompletionFailedException : Exception
{
    public TestCompletionFailedException(string detail)
        : base(detail) => Detail = detail;

    public string Detail { get; }
}

/// <summary>Everything one test-completion call needs after the model gates passed.</summary>
/// <param name="Entry">The persisted <c>model_catalog</c> row (prices, base_url, model_name).</param>
/// <param name="ApiKeyBlobId">The <c>encrypted_blobs</c> id behind <c>api_key_secret_ref</c>.</param>
/// <param name="Engine">The provider's registered <see cref="IAgentEngine"/>.</param>
public sealed record TestCompletionTarget(
    ModelCatalogEntry Entry, Guid ApiKeyBlobId, IAgentEngine Engine);

/// <summary>
/// M3T6 — the admin test-completion tool (§13.3 / SQ-M3-4 / SQ-M3-14 /
/// SQ-M3-15 / SQ-M3-17): loads the persisted model, decrypts its system-wrapped
/// key, runs ONE non-streaming completion through the M2T1
/// <see cref="IAgentEngine"/> interface (whose M3T1 default is
/// <c>EngineCallOptions.Default</c> — <c>Stream=false</c>; streaming test
/// completion is out of M3 scope, M5 territory), derives the cost from M3T4's
/// helper, writes the <c>test_model_completion</c> audit row with the prompt
/// SHA-256 (never the prompt) and returns the response projection.
/// </summary>
/// <remarks>
/// <para>
/// <b>Engine resolution is per-model-provider (SQ-M3-14).</b> The catalog's
/// <c>provider</c> value keys <see cref="IPluginRegistry.GetAgentEngine(string)"/>;
/// a provider with no registered plugin fails with a CLEAR
/// <see cref="TestCompletionFailedException"/> (§13.12 500), never an opaque
/// null-reference — M3 ships exactly one plugin and does not validate
/// <c>provider</c> at create/PATCH time.
/// </para>
/// <para>
/// <b>Cost surface (SQ-M3-17).</b> <c>providerUsage: done?.ProviderUsage</c>
/// carries <c>usage.cost</c> end-to-end; the <c>x-litellm-cost</c> HEADER
/// surface is deliberately NOT plumbed through the engine in M3
/// (<c>providerCostHeaders</c> stays null) — that lands with M5's
/// EngineCaller, and M3T4's unit suite covers header precedence. Token counts
/// ride through <see cref="TokenCounts.From(AgentDone?)"/> which PRESERVES the
/// nulls (M3T4 deviation D1): an usage-absent response yields null
/// prompt/completion tokens and a null cost, never a fabricated 0/$0.
/// </para>
/// <para>
/// <b>Admin-driven, not budgeted (task Out).</b> Q-L8's per-user soft token
/// cap is NOT enforced here — this is a manual admin exerciser, not a
/// per-message generation call — and the decrypted key lives only for the
/// duration of this one call: it is never logged, never audited and never
/// returned (the success log line carries ids, counts and duration only).
/// </para>
/// </remarks>
internal sealed class TestCompletionService(
    EmailAgentDbContext db,
    EncryptedModelApiKeyStore apiKeyStore,
    IPluginRegistry pluginRegistry,
    ICostDerivationService cost,
    IAuditLogger audit,
    ILogger<TestCompletionService> logger)
{
    /// <summary>SQ-M3-15: the sanitized <c>detail</c> budget in UTF-8 bytes (M3T2's excerpt helper).</summary>
    private const int DetailByteBudget = 256;

    /// <summary>
    /// Runs the three model gates — 404 unknown row, 400 disabled, 400
    /// config-validation when the row carries no readable key reference — and
    /// resolves the provider's engine (a clear 500 when none is registered,
    /// SQ-M3-14). Exposed so unit tests can assert the gate ordering without
    /// executing a completion.
    /// </summary>
    public async Task<TestCompletionTarget> PrepareAsync(Guid modelId, CancellationToken ct)
    {
        var entry = await db.ModelCatalog.AsNoTracking()
                .FirstOrDefaultAsync(model => model.Id == modelId, ct)
            ?? throw new ModelNotFoundException(modelId);

        if (!entry.Enabled)
        {
            throw new ModelDisabledException(modelId);
        }

        if (entry.ApiKeySecretRef is not { Length: > 0 } storedRef
            || !Guid.TryParse(storedRef, out var blobId))
        {
            // M3T3's PATCH-path posture: a catalog row whose key pointer is
            // unreadable is a §13.12 400 /errors/config-validation naming
            // api_key — never a socket attempt with an empty credential.
            throw new ConfigValidationException(
                "The model has no stored API key, so a test completion cannot authenticate.",
                new[] { new ValidationIssue("/api_key", "required") },
                OpenAICompatibleConfigSchema.Build());
        }

        var engine = pluginRegistry.GetAgentEngine(entry.Provider);
        if (engine is null)
        {
            logger.LogWarning(
                "Test completion for model {ModelId} refused: no agent engine is registered for provider {Provider}.",
                modelId, entry.Provider);
            throw new TestCompletionFailedException(
                $"No agent engine plugin is registered for provider '{entry.Provider}', "
                + "so this test completion cannot run. M3 ships the 'openai-compatible' plugin only.");
        }

        return new TestCompletionTarget(entry, blobId, engine);
    }

    /// <summary>
    /// Task step 2: load → decrypt → build the <see cref="AgentRequest"/> →
    /// drain the engine's event stream → tokens/cost → audit row → response.
    /// The duration is wall-clock from THIS method's entry (the body has
    /// already been parsed by the handler) to the final <c>AgentDone</c>
    /// (SQ-M3-4). See the type remarks for the non-streaming, budget and
    /// cost-surface contracts.
    /// </summary>
    public async Task<TestCompletionResponse> ExecuteAsync(
        Guid modelId, TestCompletionRequest request, ModelActor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        var stopwatch = Stopwatch.StartNew();

        var target = await PrepareAsync(modelId, ct);
        var entry = target.Entry;
        var promptHash = ComputePromptHash(
            request.SystemPrompt ?? string.Empty, request.UserPrompt ?? string.Empty);

        var apiKey = await apiKeyStore.ReadAsync(target.ApiKeyBlobId, ct);

        // A dictionary (not an anonymous object) keeps the member names the
        // M3T1 schema declares, independent of any naming policy (M3T2
        // precedent). This element carries the decrypted key and travels ONLY
        // into AgentContext.EngineConfigJson — in-process, [JsonIgnore],
        // redacted by AgentContext.ToString (SQ-M3-10).
        var configJson = JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            ["base_url"] = entry.BaseUrl,
            ["api_key"] = apiKey,
            ["model_name"] = entry.ModelName,
        });

        var allowedActionSchemas = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);
        if (request.Tools is not null)
        {
            foreach (var tool in request.Tools)
            {
                allowedActionSchemas[tool.Name!] = BuildToolSchema(tool);
            }
        }

        var agentRequest = new AgentRequest(
            Message: null!,
            Prompt: BuildPrompt(request.SystemPrompt ?? string.Empty, request.UserPrompt ?? string.Empty),
            AllowedActionSchemas: allowedActionSchemas,
            Context: new AgentContext(
                UserId: actor.UserId, SourceId: Guid.Empty, Ct: ct)
            {
                EngineConfigJson = configJson,
            });

        var events = new List<AgentEvent>();
        try
        {
            // The M2T1 interface method only (AGENTS.md hard constraint #8):
            // its M3T1 default is EngineCallOptions.Default — Stream=false.
            await foreach (var evt in target.Engine
                               .CompleteAsync(agentRequest, ct)
                               .ConfigureAwait(false))
            {
                events.Add(evt);
            }
        }
        catch (EngineCallException ex)
        {
            // Log ids/HTTP status only — NEVER the exception message, the
            // response body or the detail: the provider body could echo a
            // credential (the sanitized detail is for the RESPONSE, not the
            // log — task Risks: api_key is never logged).
            logger.LogWarning(
                "Test completion for model {ModelId} failed with engine HTTP status {StatusCode}.",
                modelId, ex.StatusCode);
            throw new TestCompletionFailedException(
                SanitizeDetail($"{ex.Message} | {ex.ResponseBody}"));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // Review fix: transport failures (connection refused, DNS,
            // truncated read) never surface as EngineCallException — without
            // this arm they escape to the global handler as a generic
            // /errors/server-error. They map to the SAME typed 500 with a
            // SANITIZED detail (an exception message can echo a URL or a key
            // shape), and the warning carries the exception TYPE only.
            logger.LogWarning(
                "Test completion for model {ModelId} failed with a transport error ({ExceptionType}).",
                modelId, ex.GetType().Name);
            throw new TestCompletionFailedException(SanitizeDetail(ex.Message));
        }

        // AgentToolCall.Arguments / AgentDone.ProviderUsage are Clone()d out
        // of their JsonDocument by OpenAIResponseMapper (its documented
        // lifetime contract); Clone() again here keeps this layer independent
        // of that guarantee (task snippet parity).
        var responseText = string.Concat(
            events.OfType<AgentTextChunk>().Select(chunk => chunk.Text));
        var toolCalls = events.OfType<AgentToolCall>()
            .Select(toolCall => new ToolCallDto
            {
                Name = toolCall.Name,
                Arguments = toolCall.Arguments.Clone(),
            })
            .ToList();
        var done = events.OfType<AgentDone>().LastOrDefault();

        // M3T4 deviation D1: From() preserves nulls — NEVER coalesce with
        // `?? 0`, which would fabricate "0 tokens, $0" for usage-absent
        // responses (cost must stay NULL instead).
        var tokens = TokenCounts.From(done);
        var derivedCost = cost.DeriveCost(
            tokens, entry, providerCostHeaders: null, providerUsageBlock: done?.ProviderUsage);
        stopwatch.Stop();
        var durationMs = (int)stopwatch.ElapsedMilliseconds;

        // SQ-M3-4 / Q-O17a: the exact documented field set — ids, duration,
        // token counts, cost, tool-call count, the prompt SHA-256 (NEVER the
        // prompt text) and the (model_name, base_url) correlation pair. Actor
        // role + IP ride on the row itself (M3T3 parity), not in metadata.
        await audit.LogAsync(
            new AuditEvent(
                ActorUserId: actor.UserId,
                ActorRole: actor.Role,
                Action: AuditActions.TestModelCompletion,
                TargetType: "model_catalog",
                TargetId: modelId.ToString(),
                MetadataJson: JsonSerializer.Serialize(new
                {
                    model_id = modelId,
                    actor_user_id = actor.UserId,
                    duration_ms = durationMs,
                    prompt_tokens = tokens.PromptTokens,
                    completion_tokens = tokens.CompletionTokens,
                    total_tokens = tokens.EffectiveTotalTokens,
                    cost_usd = derivedCost,
                    tool_call_count = toolCalls.Count,
                    prompt_sha256 = promptHash,
                    model_name = entry.ModelName,
                    base_url = entry.BaseUrl,
                }),
                Ip: actor.Ip),
            ct);

        // Success-only, credential-free log line (task Risks: the DoD unit
        // test scans ILogger invocations for the key / prompt substrings).
        logger.LogInformation(
            "Test completion for model {ModelId} by {ActorUserId} finished in {DurationMs} ms "
            + "with {ToolCallCount} tool call(s) and {PromptTokens} prompt tokens.",
            modelId, actor.UserId, durationMs, toolCalls.Count, tokens.PromptTokens);

        return new TestCompletionResponse
        {
            ResponseText = responseText,
            ToolCalls = toolCalls.Count == 0 ? null : toolCalls,
            PromptTokens = tokens.PromptTokens,
            CompletionTokens = tokens.CompletionTokens,
            TotalTokens = tokens.EffectiveTotalTokens,
            CostUsd = derivedCost,
            DurationMs = durationMs,
        };
    }

    /// <summary>
    /// SQ-M3-4: <c>SHA-256("{system_prompt}\n{user_prompt}")</c> as a
    /// 64-character lowercase hex string. The RAW prompt never leaves this
    /// method — only its hash reaches the audit row (task acceptance
    /// criterion; exercised by PromptHashTests).
    /// </summary>
    internal static string ComputePromptHash(string systemPrompt, string userPrompt)
        => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes($"{systemPrompt}\n{userPrompt}")))
            .ToLowerInvariant();

    /// <summary>
    /// SQ-M3-15 (Q-L14): redacts the two common key shapes an error body could
    /// echo (<c>sk-…</c> / <c>sk-ant-…</c> → <c>sk-REDACTED</c>,
    /// <c>Bearer …</c> → <c>Bearer REDACTED</c>) BEFORE the detail leaves the
    /// process, then clamps it to 256 UTF-8 bytes through M3T2's shared,
    /// boundary-safe excerpt helper (task error table). Regex first, then
    /// truncate — so the truncation can never re-expose a redacted tail.
    /// </summary>
    internal static string SanitizeDetail(string detail)
    {
        var sanitized = Regex.Replace(
            detail, @"sk-[A-Za-z0-9_\-]+", "sk-REDACTED",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        sanitized = Regex.Replace(
            sanitized, @"Bearer\s+[A-Za-z0-9_\-\.]+", "Bearer REDACTED",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        return Utf8Excerpt.Truncate(sanitized, DetailByteBudget);
    }

    /// <summary>
    /// SQ-M3-9 markers (mirrored from M3T1's private splitter / M3T2's
    /// <see cref="ProbeRequestBuilder"/>): a system prompt rides as a SYSTEM
    /// message ahead of the user prompt; absent or empty, the user prompt goes
    /// through unchanged.
    /// </summary>
    private static string BuildPrompt(string systemPrompt, string userPrompt)
        => string.IsNullOrEmpty(systemPrompt)
            ? userPrompt
            : $"SYSTEM:\n{systemPrompt}\n\nUSER:\n{userPrompt}";

    /// <summary>
    /// One allowed-action schema per tool (M3T2 precedent): the DTO's
    /// <c>parameters</c> document IS the schema, with the DTO's
    /// <c>description</c> injected at the schema root when the document does
    /// not carry one — M3T1's request builder reads the tool description from
    /// exactly there. Validation already guaranteed the document materializes
    /// (TestCompletionRequestValidator), so this cannot throw on the
    /// request path.
    /// </summary>
    private static JsonSchema BuildToolSchema(ToolSpecDto tool)
    {
        var raw = tool.Parameters?.ToJsonString() ?? "{}";
        if (!string.IsNullOrWhiteSpace(tool.Description)
            && JsonNode.Parse(raw) is JsonObject document
            && !document.ContainsKey("description"))
        {
            document["description"] = tool.Description;
            raw = document.ToJsonString();
        }

        return JsonSchema.FromText(raw);
    }
}
