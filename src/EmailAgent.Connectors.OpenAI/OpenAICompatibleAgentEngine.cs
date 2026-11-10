using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using EmailAgent.Contracts;
using EmailAgent.Contracts.AgentEngines;
using EmailAgent.Contracts.Schema;
using EmailAgent.Connectors.OpenAI.Http;
using Json.Schema;

namespace EmailAgent.Connectors.OpenAI;

/// <summary>
/// M3T1 — the DEFAULT OpenAI-compatible agent engine plugin (§5 / §9 / §11):
/// implements <see cref="IAgentEngine"/> verbatim against
/// <c>POST {base_url}/chat/completions</c>, speaking the streaming SSE shape
/// when <c>stream=true</c> and the non-streamed JSON shape otherwise, and
/// mapping responses onto the M2T1 <see cref="AgentEvent"/> hierarchy without
/// adding a variant. <see cref="ValidateConfigAsync"/> is a schema-level check
/// only — the live endpoint probe is M3T2's job.
/// </summary>
/// <remarks>
/// <para>
/// <b>Call surface:</b> the interface method delegates to the plugin-local
/// <see cref="CompleteAsync(AgentRequest, EngineCallOptions, CancellationToken)"/>
/// overload with <see cref="EngineCallOptions.Default"/> (<c>Stream=false</c>),
/// so the M2T1 connector contract stays verbatim (AGENTS.md hard constraint
/// #8) while in-process callers that want SSE resolve this concrete type.
/// </para>
/// <para>
/// <b>Configuration:</b> per-call from <c>req.Context.EngineConfigJson</c>
/// (SQ-M3-10) — never cached across calls, so the <c>api_key</c> is read fresh
/// from the caller's context every time. Missing config is a programming
/// error at the call site and throws <see cref="InvalidOperationException"/>;
/// an HTTP non-2xx or an unmappable body throws <see cref="EngineCallException"/>.
/// </para>
/// <para>
/// <b>Transport:</b> the constructor-injected <see cref="HttpClient"/> comes
/// from the loader's DI wiring (<c>PluginLoader</c> instantiates the declared
/// entry type through <c>ActivatorUtilities</c>; Program.cs registers an
/// <c>IHttpClientFactory</c>-backed client). Responses are read with
/// <see cref="HttpCompletionOption.ResponseHeadersRead"/> so the SSE body is
/// streamed frame by frame instead of buffered.
/// </para>
/// </remarks>
public sealed class OpenAICompatibleAgentEngine : IAgentEngine
{
    /// <summary>
    /// The whole-call budget applied when <see cref="EngineCallOptions.Timeout"/>
    /// is not set. It exists because <see cref="HttpClient.Timeout"/> is
    /// implemented as a timer on a token source that is disposed once the
    /// response HEADERS arrive under
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/> — it bounds
    /// connect/send/headers only, never the body phase, so the engine carries
    /// its own deadline across every read (dotnet/runtime#54674).
    /// </summary>
    private static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromMinutes(30);

    private readonly HttpClient _httpClient;

    /// <summary>Creates the engine around the HTTP transport supplied by plugin DI (or a test fixture).</summary>
    /// <param name="httpClient">Client used for every call; its handler decides whether that is a real socket or an in-memory test server.</param>
    public OpenAICompatibleAgentEngine(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    /// <inheritdoc />
    public string TypeId => "openai-compatible";

    /// <inheritdoc />
    public JsonSchema ConfigSchema => OpenAICompatibleConfigSchema.Build();

    /// <summary>
    /// Schema-level configuration check (M3T1 step 10) — never opens a socket.
    /// The <see cref="ConfigSchema"/> gate runs first so field-level issues
    /// reach the caller untouched; the typed parse afterwards asserts what the
    /// schema's non-evaluated <c>format</c> annotation cannot (absolute
    /// http(s) <c>base_url</c>).
    /// </summary>
    public Task<ValidationResult> ValidateConfigAsync(JsonElement config, CancellationToken ct)
    {
        var schemaResult = JsonSchemaValidator.Validate(ConfigSchema, config);
        if (!schemaResult.Ok)
        {
            return Task.FromResult(schemaResult);
        }

        try
        {
            _ = OpenAICompatibleConfig.FromJson(config);
        }
        catch (JsonException ex)
        {
            return Task.FromResult(
                ValidationResult.Failure("invalid config", new[] { new ValidationIssue("$", ex.Message) }));
        }

        return Task.FromResult(ValidationResult.Success());
    }

    /// <inheritdoc />
    public IAsyncEnumerable<AgentEvent> CompleteAsync(AgentRequest req, CancellationToken ct) =>
        CompleteAsync(req, EngineCallOptions.Default, ct);

    /// <summary>
    /// Runs one completion, yielding events as the response arrives:
    /// streamed frames (or the single JSON body when
    /// <c>options.Stream</c> is <see langword="false"/>) mapped to
    /// <see cref="AgentTextChunk"/> / <see cref="AgentToolCall"/> /
    /// <see cref="AgentDone"/>. In streaming mode every terminal frame is held
    /// back until the protocol ends (the <c>[DONE]</c> sentinel or end of
    /// stream — merged so <c>include_usage</c> bookkeeping survives) and then
    /// yielded last, so a stream that never carries <c>finish_reason</c> fails
    /// instead of ending in a silent completion; the sentinel ends the read,
    /// so completion never waits on socket EOF. The whole call (send, headers
    /// and body) is bounded by <see cref="EngineCallOptions.Timeout"/> or
    /// <see cref="DefaultCallTimeout"/>; a deadline or transport timeout at
    /// any phase is reported as a 408 engine error, never as a cancellation.
    /// The transmitted body carries <c>max_tokens: N</c> exactly when
    /// <see cref="EngineCallOptions.MaxTokens"/> is set (M3T2's probe passes
    /// SQ-M3-3's cost bound of 1; <see cref="EngineCallOptions.Default"/> keeps
    /// the field off the wire).
    /// Throws <see cref="InvalidOperationException"/>
    /// when the caller supplied no engine configuration,
    /// <see cref="EngineCallException"/> on an HTTP error, an unmappable
    /// body, a transport timeout (408), or a stream that ends without a
    /// terminal frame, and
    /// <see cref="OperationCanceledException"/> when
    /// <paramref name="ct"/> fires (checked between frames by the SSE parser).
    /// </summary>
    public async IAsyncEnumerable<AgentEvent> CompleteAsync(
        AgentRequest req,
        EngineCallOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(options);

        if (req.Context.EngineConfigJson is not { } configElement)
        {
            throw new InvalidOperationException(
                "EngineConfigJson is required to call the OpenAI-compatible engine — the caller must populate "
                + "AgentContext.EngineConfigJson from the persisted model catalog before invoking the engine (M3T1 SQ-M3-10).");
        }

        OpenAICompatibleConfig config;
        try
        {
            config = OpenAICompatibleConfig.FromJson(configElement);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"EngineConfigJson is invalid: {ex.Message}", ex);
        }

        // One per-call deadline bounds the WHOLE call. HttpClient.Timeout
        // stops at the response HEADERS under ResponseHeadersRead (its timer
        // lives on a token source disposed at header time), so without this
        // the body phase would have no bound at all and a provider that
        // answers 200 and then stalls could wedge the caller forever. Every
        // read below uses the deadline token; the catches translate a fired
        // deadline into 408 as long as the CALLER's own token is still live.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.Timeout ?? DefaultCallTimeout);
        CancellationToken deadlineToken = deadline.Token;

        var body = OpenAIRequestBuilder.Build(req, config, options.Stream, options.MaxTokens);
        var endpoint = config.BaseUrl.TrimEnd('/') + "/chat/completions";

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

        // A transport or deadline timeout is NOT a caller cancellation: the
        // helper below translates it into the engine's own failure type so
        // callers can classify it as retryable instead of as a shutdown.
        using var response = await SendGuardedAsync(message, deadlineToken, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody;
            try
            {
                errorBody = await response.Content.ReadAsStringAsync(deadlineToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new EngineCallException(
                    statusCode: 408,
                    responseBody: string.Empty,
                    detail: "engine call timed out");
            }

            throw new EngineCallException((int)response.StatusCode, errorBody);
        }

        if (options.Stream)
        {
            Stream responseStream;
            try
            {
                responseStream = await response.Content.ReadAsStreamAsync(deadlineToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new EngineCallException(
                    statusCode: 408,
                    responseBody: string.Empty,
                    detail: "engine call timed out");
            }

            using Stream stream = responseStream;
            await using IAsyncEnumerator<SseFrame> frames =
                SseStreamParser.ParseAsync(stream, deadlineToken).GetAsyncEnumerator(deadlineToken);

            AgentDone? terminal = null;
            while (true)
            {
                // Events are COLLECTED under the guard and yielded outside it:
                // `yield return` cannot sit in a try block that has a catch,
                // yet the per-call deadline (or a transport timeout) can
                // surface from any body read and must always be reported as
                // EngineCallException(408) rather than as a cancellation.
                List<AgentEvent> pending = [];
                var ended = false;
                try
                {
                    if (!await frames.MoveNextAsync().ConfigureAwait(false))
                    {
                        ended = true;
                    }
                    else
                    {
                        SseFrame frame = frames.Current;
                        if (frame.Data.Trim().Equals("[DONE]", StringComparison.Ordinal))
                        {
                            // The sentinel ends the protocol: everything before
                            // it (content and the usage tail) is already mapped,
                            // so stop reading instead of draining to socket EOF
                            // — the held terminal event must not depend on the
                            // provider closing the connection.
                            ended = true;
                        }
                        else
                        {
                            foreach (var evt in OpenAIResponseMapper.MapFrame(frame))
                            {
                                if (evt is AgentDone done)
                                {
                                    // The `include_usage` tail can follow the frame carrying
                                    // `finish_reason`: hold every terminal frame until the
                                    // stream ends (so the tail is still read) and let the
                                    // merged last one win, keeping the usage bookkeeping.
                                    terminal = PreferWithUsage(terminal, done);
                                }
                                else
                                {
                                    pending.Add(evt);
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new EngineCallException(
                        statusCode: 408,
                        responseBody: string.Empty,
                        detail: "engine call timed out");
                }

                foreach (AgentEvent evt in pending)
                {
                    yield return evt;
                }

                if (ended)
                {
                    break;
                }
            }

            if (terminal is null)
            {
                // The transport succeeded but the provider never signalled a
                // terminal frame: surface that as a protocol failure rather than
                // as a silent, usage-less completion.
                throw new EngineCallException(
                    statusCode: 200,
                    responseBody: string.Empty,
                    detail: "SSE stream ended without a finish_reason frame");
            }

            yield return terminal;
            yield break;
        }

        string text;
        try
        {
            text = await response.Content.ReadAsStringAsync(deadlineToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The buffered body read is subject to the per-call deadline —
            // report it as 408, not as a caller cancellation.
            throw new EngineCallException(
                statusCode: 408,
                responseBody: string.Empty,
                detail: "engine call timed out");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new EngineCallException(
                200, text, $"response body is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            foreach (var evt in OpenAIResponseMapper.MapNonStreaming(document))
            {
                yield return evt;
            }
        }
    }

    /// <summary>
    /// Sends the request with <see cref="HttpCompletionOption.ResponseHeadersRead"/>,
    /// converting a fired deadline or transport timeout into an
    /// <see cref="EngineCallException"/> (408) — an
    /// <see cref="OperationCanceledException"/> raised while the CALLER's own
    /// token is still live means the transport gave up, not the caller, so it
    /// must not masquerade as a shutdown.
    /// </summary>
    /// <param name="message">The prepared POST.</param>
    /// <param name="deadlineToken">The per-call deadline token used for the send.</param>
    /// <param name="callerToken">The caller's cancellation token — consulted for the failure classification only.</param>
    /// <returns>The response, headers-only.</returns>
    private async Task<HttpResponseMessage> SendGuardedAsync(
        HttpRequestMessage message, CancellationToken deadlineToken, CancellationToken callerToken)
    {
        try
        {
            return await _httpClient
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadlineToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            throw new EngineCallException(
                statusCode: 408,
                responseBody: string.Empty,
                detail: "engine call timed out");
        }
    }

    /// <summary>
    /// Merges two terminal <see cref="AgentDone"/> frames produced by one SSE
    /// stream: the frame carrying usage bookkeeping wins, and failing that the
    /// later frame does — so an <c>include_usage</c> tail arriving after
    /// <c>finish_reason</c> is preserved instead of overwriting a populated
    /// <see cref="AgentDone.ProviderUsage"/> with <see langword="null"/>.
    /// </summary>
    /// <param name="current">The terminal frame seen so far, or <see langword="null"/>.</param>
    /// <param name="incoming">The terminal frame just mapped from the stream.</param>
    /// <returns>The merged terminal frame to yield at end of stream.</returns>
    private static AgentDone PreferWithUsage(AgentDone? current, AgentDone incoming) =>
        current is null
            ? incoming
            : incoming.ProviderUsage is not null || current.ProviderUsage is null
                ? incoming
                : current;
}
