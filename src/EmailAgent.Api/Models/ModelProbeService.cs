using System.Diagnostics;
using System.Text.Json;
using EmailAgent.Api.Config;
using EmailAgent.Contracts.AgentEngines;
using EmailAgent.Connectors.OpenAI;
using EmailAgent.Connectors.OpenAI.Http;
using EmailAgent.Core.Audit;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EmailAgent.Api.Models;

/// <summary>
/// M3T2 — the tool-calling capabilities probe (§11 / SQ-M3-3 / Q-L4): a
/// within-window cache lookup on <c>model_catalog</c>, otherwise one minimal
/// non-streaming completion through M3T1's engine (the <c>IAgentEngine</c>
/// seam, dispatched to the options-carrying overload so the wire body carries
/// SQ-M3-3's <c>max_tokens: 1</c> bound — Q-L15) asserting the
/// <c>ping</c> pass condition, with a fail-only
/// <c>ModelProbeFailed</c> audit row (Q-O17 / SQ-M3-11).
/// </summary>
/// <remarks>
/// <para>
/// <b>Internal on purpose (M3T2 deviation D3):</b> the signature takes M3T1's
/// <c>OpenAICompatibleConfig</c>, which is an <c>internal</c> test seam in the
/// Connectors.OpenAI assembly — CS0051 forbids exposing it through a public
/// Api member even across <c>InternalsVisibleTo</c>. M3T3 (the catalog
/// endpoint) lives in the same assembly, and the suites are friend assemblies.
/// </para>
/// <para>
/// <b>No HTTP client here:</b> the engine call goes through the injected
/// <c>Func&lt;IAgentEngine&gt;</c> factory (resolved per probe from
/// <c>IPluginRegistry</c>), so M3T2 contains no direct transport code and unit
/// tests can substitute a canned-event fake.
/// </para>
/// </remarks>
internal sealed class ModelProbeService : IModelProbeService
{
    /// <summary>The plugin/engine id whose instance the factory resolves (M3T1's DEFAULT engine).</summary>
    public const string OpenAiCompatibleEngineTypeId = "openai-compatible";

    private const string FailTargetType = "model_catalog";

    private const string ReasonNoToolCalls = "no_tool_calls";
    private const string ReasonUnexpectedToolNames = "unexpected_tool_names";

    /// <summary>SQ-M3-3: the response excerpt budget in UTF-8 bytes.</summary>
    private const int ResponseExcerptBytes = 256;

    /// <summary>
    /// The canonical probe call shape (SQ-M3-3 / Q-L15): non-streamed, with the
    /// <c>max_tokens: 1</c> cost bound that the interface method alone cannot
    /// carry (<c>IAgentEngine.CompleteAsync(AgentRequest, CancellationToken)</c>
    /// has no options parameter — AGENTS.md hard constraint #8, so the probe
    /// resolves the concrete M3T1 engine for the bounded overload).
    /// <see cref="EngineCallOptions.Default"/> stays untouched for every other
    /// caller: <c>max_tokens</c> is off the wire unless a call opts in.
    /// </summary>
    private static readonly EngineCallOptions ProbeCallOptions = new(
        Stream: false,
        MaxTokens: ProbeRequestBuilder.ProbeMaxTokens);

    private readonly EmailAgentDbContext _db;
    private readonly IAuditLogger _audit;
    private readonly IOptions<ModelOptions> _options;
    private readonly TimeProvider _clock;
    private readonly Func<IAgentEngine> _engineFactory;

    /// <summary>Creates the probe service.</summary>
    /// <param name="db">The request-scoped EF context sharing M3T3's catalog table (M1T2).</param>
    /// <param name="audit">Audit sink; consulted only on <see cref="ProbeOutcome.Fail"/>.</param>
    /// <param name="options"><c>Model</c> section options (the re-probe window, SQ-M3-3).</param>
    /// <param name="clock">UTC clock (registered as <see cref="TimeProvider.System"/>); seam for cache-window tests.</param>
    /// <param name="engineFactory">Resolves the <c>openai-compatible</c> <see cref="IAgentEngine"/> per probe call.</param>
    public ModelProbeService(
        EmailAgentDbContext db,
        IAuditLogger audit,
        IOptions<ModelOptions> options,
        TimeProvider clock,
        Func<IAgentEngine> engineFactory)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _engineFactory = engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));
    }

    /// <inheritdoc />
    public async Task<ProbeResult> ProbeAsync(
        OpenAICompatibleConfig config, Guid? correlationModelId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        var now = _clock.GetUtcNow();
        var window = _options.Value.ProbeReprobeWindow;

        // Cache key is exactly (base_url, model_name) + Pass + within window:
        // an api_key rotation does not change tool-calling capability, and
        // correlationModelId is audit-only — neither participates in the key
        // (task Risks). A stored Fail is never cache-eligible (SQ-M3-3).
        var cachedRow = await _db.ModelCatalog
            .AsNoTracking()
            .Where(m => m.BaseUrl == config.BaseUrl
                     && m.ModelName == config.ModelName
                     && m.ToolCallingProbeResult == ToolCallingProbeResult.Pass
                     && m.ToolCallingProbedAt >= now - window)
            .OrderByDescending(m => m.ToolCallingProbedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (cachedRow is not null)
        {
            // Row timestamp preserves WHEN the honored probe ran; `now` is this
            // call's probed_at (task step 7 — PassFromCacheResult(cachedFrom, probedAt)).
            return ProbeResult.PassFromCacheResult(cachedRow.ToolCallingProbedAt!.Value, now);
        }

        // The canonical SQ-M3-3 wire body for the audit row's request_excerpt
        // — since the Q-L15 fix this is also the shape the engine transmits
        // (ProbeCallOptions adds max_tokens: 1 to the engine's builder output;
        // the builder's documented `parameters` flattening and member order
        // remain M3T1's request-shape contract — M3T2 deviation D7).
        // The AgentRequest above carries the API key inside EngineConfigJson;
        // we serialize ONLY the wire body, so the plaintext key can never land
        // in audit_log.metadata_json (the key travels in the engine's
        // Authorization: Bearer header, never in a body).
        var probeRequest = ProbeRequestBuilder.BuildProbeRequest(config.BaseUrl, config.ApiKey, config.ModelName);
        var wireBody = ProbeRequestBuilder.BuildWireBody(probeRequest);
        var requestJson = JsonSerializer.Serialize(wireBody, OpenAIJsonOptions.Default);

        var stopwatch = Stopwatch.StartNew();
        AgentDone? lastDone = null;

        // Materialized DURING the drain, not after it: AgentToolCall.Arguments
        // is a JsonElement tied to its JsonDocument (the AgentEvent lifetime
        // contract), so the raw text is read while the event is still live
        // instead of relying on the engine having cloned the element.
        var toolCalls = new List<(string Name, string ArgumentsJson)>();
        var responseTextChunks = new List<string>();
        var engine = _engineFactory(); // IAgentEngine is not IAsyncDisposable — plain local.

        // The canonical probe call (SQ-M3-3) goes through the concrete engine's
        // options-carrying overload so the transmitted body carries
        // max_tokens: 1 (Q-L15). The interface itself has no options parameter
        // (the M2T1 contract stays verbatim), so any other IAgentEngine — the
        // unit suite's canned-event fake, a future plugin — takes the
        // interface default with no max_tokens member.
        IAsyncEnumerable<AgentEvent> events = engine is OpenAICompatibleAgentEngine openAiEngine
            ? openAiEngine.CompleteAsync(probeRequest, ProbeCallOptions, ct)
            : engine.CompleteAsync(probeRequest, ct);
        await foreach (var evt in events.ConfigureAwait(false))
        {
            switch (evt)
            {
                case AgentTextChunk chunk:
                    responseTextChunks.Add(chunk.Text);
                    break;
                case AgentToolCall toolCall:
                    toolCalls.Add((toolCall.Name, toolCall.Arguments.GetRawText()));
                    break;
                case AgentDone doneEvent:
                    lastDone = doneEvent;
                    break;
            }
        }

        stopwatch.Stop();
        var durationMs = (int)stopwatch.ElapsedMilliseconds;

        // The excerpt is RECONSTRUCTED from the AgentEvent stream, not the raw
        // HTTP body — so it can never echo a provider-side credential. Budget:
        // first 256 UTF-8 bytes, cut at a character boundary (SQ-M3-3).
        var responseBody = JsonSerializer.Serialize(
            new
            {
                choices = new[]
                {
                    new
                    {
                        message = new
                        {
                            content = string.Concat(responseTextChunks),
                            tool_calls = toolCalls.Select(tc => new
                            {
                                function = new
                                {
                                    name = tc.Name,
                                    arguments = tc.ArgumentsJson,
                                },
                            }).ToArray(),
                        },
                    },
                },
                usage = lastDone is { } done
                    ? new { prompt_tokens = done.PromptTokens, completion_tokens = done.CompletionTokens }
                    : null,
            },
            OpenAIJsonOptions.Default);
        var responseExcerpt = Utf8Excerpt.Truncate(responseBody, ResponseExcerptBytes);

        // SQ-M3-3 pass condition (verbatim): the tool_calls array is NON-EMPTY
        // and every entry's function.name equals "ping" — a mixed/foreign name
        // is a fail, not a pass (the milestone's dispatcher wording; the task
        // snippet's bare .Any() would under-reject).
        var pass = toolCalls.Count > 0 && toolCalls.All(tc => tc.Name == ProbeRequestBuilder.ProbeToolName);

        var result = pass
            ? ProbeResult.PassResult(requestJson, responseExcerpt, now)
            : ProbeResult.FailResult(
                reason: toolCalls.Count == 0 ? ReasonNoToolCalls : ReasonUnexpectedToolNames,
                requestExcerpt: requestJson,
                responseExcerpt: responseExcerpt,
                probedAt: now);

        if (result.Outcome == ProbeOutcome.Fail)
        {
            await EmitFailAuditAsync(correlationModelId, config, result, durationMs, ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Fail-only emission (Q-O17 verbatim: M3 has exactly one probe audit
    /// action). <c>target_id</c> is the caller's correlation id — the model id
    /// on the PATCH path, <c>NULL</c> on the POST path where no row exists yet
    /// (SQ-M3-11). Actor fields stay NULL: a probe failure is a system event
    /// (§6). The metadata carries base_url, model_name, reason, the canonical
    /// request body (never the api_key), the clamped response excerpt and the
    /// engine-call duration; the fixed probe prompt appears only inside that
    /// canonical body — it is wire shape, never user data.
    /// </summary>
    private async Task EmitFailAuditAsync(
        Guid? modelId,
        OpenAICompatibleConfig config,
        ProbeResult result,
        int durationMs,
        CancellationToken ct)
    {
        var metadataJson = JsonSerializer.Serialize(
            new
            {
                base_url = config.BaseUrl,
                model_name = config.ModelName,
                reason = result.Reason,
                request_excerpt = result.RequestExcerpt,
                response_excerpt = result.ResponseExcerpt,
                duration_ms = durationMs,
            });

        await _audit.LogAsync(
                new AuditEvent(
                    ActorUserId: null,
                    ActorRole: null,
                    Action: AuditActions.ModelProbeFailed,
                    TargetType: FailTargetType,
                    TargetId: modelId?.ToString(),
                    MetadataJson: metadataJson,
                    Ip: null),
                ct)
            .ConfigureAwait(false);
    }
}
