using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EmailAgent.Contracts;
using EmailAgent.Contracts.AgentEngines;
using EmailAgent.Contracts.Plugins;
using EmailAgent.Contracts.Schema;
using EmailAgent.Connectors.OpenAI;
using EmailAgent.Infrastructure.Plugins;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Connectors.OpenAI;

/// <summary>
/// M3T1 matrix for <see cref="OpenAICompatibleAgentEngine"/> itself: the M2T1
/// identity surface (<c>TypeId</c>, the single-source-of-truth
/// <c>ConfigSchema</c>), the schema-level <c>ValidateConfigAsync</c> (no socket
/// ever), the loud failure when the caller forgot
/// <c>AgentContext.EngineConfigJson</c> (SQ-M3-10), the streaming terminal
/// bookkeeping (hold-and-merge of <c>finish_reason</c> + the
/// <c>include_usage</c> tail, and the protocol failure when neither arrives),
/// and the shipped <c>plugin.json</c> that declares the engine class with
/// wildcard egress (SQ-M3-5). The streaming cases run against in-memory
/// stubs (<see cref="SseStubHandler"/>, plus the hung-body / stalled-body
/// stubs that pin the per-call deadline), live HTTP behaviour is covered by
/// the integration tests against the in-process <c>OpenAIMock</c>.
/// </summary>
[Trait("Category", "M3T1")]
public class OpenAICompatibleAgentEngineTests
{
    [Fact]
    public void OpenAICompatibleAgentEngine_TypeId_IsOpenAiCompatible()
    {
        using var httpClient = new HttpClient();

        var engine = new OpenAICompatibleAgentEngine(httpClient);

        engine.TypeId.Should().Be("openai-compatible");
        typeof(OpenAICompatibleAgentEngine).GetInterfaces()
            .Should().Contain(typeof(IAgentEngine), "the plugin implements the M2T1 contract and nothing else");
        typeof(OpenAICompatibleAgentEngine).GetInterfaces()
            .Should().NotContain(i => i.Name == "IMessageSource" || i.Name == "IActionHandler");
    }

    [Fact]
    public void OpenAICompatibleAgentEngine_ConfigSchema_ReusesTheContractsSchema()
    {
        using var httpClient = new HttpClient();

        var engine = new OpenAICompatibleAgentEngine(httpClient);

        engine.ConfigSchema.Should().BeSameAs(
            OpenAICompatibleConfigSchema.Build(),
            "the plugin's ConfigSchema delegates to the single source of truth in EmailAgent.Contracts");
    }

    [Fact]
    public async Task OpenAICompatibleAgentEngine_ValidateConfigAsync_ValidConfig_Succeeds()
    {
        using var httpClient = new HttpClient();
        var engine = new OpenAICompatibleAgentEngine(httpClient);
        using JsonDocument doc = JsonDocument.Parse(
            """
            {"base_url":"https://llm.example.test/v1","api_key":"sk-test-redacted","model_name":"gpt-4o-mini"}
            """);

        var result = await engine.ValidateConfigAsync(doc.RootElement, CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.Issues.Should().BeNull();
    }

    [Fact]
    public async Task OpenAICompatibleAgentEngine_ValidateConfigAsync_NonHttpBaseUrl_FailsSchemaGate()
    {
        using var httpClient = new HttpClient();
        var engine = new OpenAICompatibleAgentEngine(httpClient);
        using JsonDocument doc = JsonDocument.Parse(
            """
            {"base_url":"ftp://llm.example.test/v1","api_key":"sk-test-redacted","model_name":"gpt-4o-mini"}
            """);

        var result = await engine.ValidateConfigAsync(doc.RootElement, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Issues!.Should().Contain(new ValidationIssue("/base_url", "pattern"));
    }

    [Fact]
    public async Task OpenAICompatibleAgentEngine_ValidateConfigAsync_SchemaPassingButUnparsableBaseUrl_FailsTypedCheck()
    {
        // "http://" satisfies the schema's ^https?:// pattern but is not an
        // absolute URI — the typed FromJson assertion after the schema gate is
        // what rejects it, with the documented ValidationIssue("$", ...) shape.
        using var httpClient = new HttpClient();
        var engine = new OpenAICompatibleAgentEngine(httpClient);
        using JsonDocument doc = JsonDocument.Parse(
            """
            {"base_url":"http://","api_key":"sk-test-redacted","model_name":"gpt-4o-mini"}
            """);

        var result = await engine.ValidateConfigAsync(doc.RootElement, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Issues!.Should().ContainSingle()
            .Which.Should().Be(new ValidationIssue("$", "base_url must be an absolute http(s) URL."));
    }

    [Fact]
    public async Task OpenAICompatibleAgentEngine_CompleteAsync_MissingEngineConfigJson_Throws()
    {
        using var httpClient = new HttpClient();
        var engine = new OpenAICompatibleAgentEngine(httpClient);
        var req = OpenAIFixtureFiles.Request() with
        {
            // The M2T1 default context — no EngineConfigJson was populated.
            Context = new AgentContext(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None),
        };

        Func<Task> act = async () => await DrainAsync(engine.CompleteAsync(req, CancellationToken.None));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*EngineConfigJson is required*", "SQ-M3-10: the caller must populate the engine config");
    }

    [Fact]
    public async Task OpenAICompatibleAgentEngine_CompleteAsync_Stream_TrailingUsageTail_YieldsSingleAgentDoneWithUsage()
    {
        // finish_reason arrives BEFORE the `include_usage` tail chunk: the
        // engine must hold the terminal event until the stream ends, merge the
        // tail's usage into it, and yield exactly one AgentDone (M3T1 review
        // fix — the counts must not be dropped with the consumed tail frame).
        var handler = new SseStubHandler(
            "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"},\"finish_reason\":null}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
            + "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":34,\"total_tokens\":46}}\n\n"
            + "data: [DONE]\n\n");
        using var httpClient = new HttpClient(handler);
        var engine = new OpenAICompatibleAgentEngine(httpClient);

        List<AgentEvent> events = await DrainAsync(engine.CompleteAsync(
            OpenAIFixtureFiles.Request(), new EngineCallOptions(Stream: true), CancellationToken.None));

        events.Should().HaveCount(2, "one text chunk then exactly one merged terminal AgentDone");
        events[0].Should().BeOfType<AgentTextChunk>().Which.Text.Should().Be("hello");
        var done = events[1].Should().BeOfType<AgentDone>().Subject;
        done.PromptTokens.Should().Be(12);
        done.CompletionTokens.Should().Be(34);
        done.TotalTokens.Should().Be(46, "the include_usage tail's usage must survive the merge");
        done.ProviderUsage.Should().NotBeNull();

        // The outbound wire shape the stub observed (review NIT: the request
        // itself must carry stream=true and the Bearer credential).
        StubRequest? maybeSent = handler.Request;
        maybeSent.Should().NotBeNull("exactly one engine call was made");
        StubRequest sent = maybeSent!;
        sent.Method.Should().Be(HttpMethod.Post);
        AuthenticationHeaderValue? maybeAuthorization = sent.Authorization;
        maybeAuthorization.Should().NotBeNull();
        AuthenticationHeaderValue authorization = maybeAuthorization!;
        authorization.Scheme.Should().Be("Bearer");
        authorization.Parameter.Should().Be("sk-test-redacted");
        sent.Body.Should().Contain("\"stream\":true");
        sent.Body.Should().Contain(
            "\"stream_options\":{\"include_usage\":true}",
            "a streamed call must ask for the usage tail the terminal merge depends on");
    }

    [Fact]
    public async Task OpenAICompatibleAgentEngine_CompleteAsync_TransportTimeout_ThrowsEngineCallException408()
    {
        // A transport timeout is not the caller's cancellation: it must
        // surface as a retryable EngineCallException(408) instead of an
        // OperationCanceledException that callers read as a shutdown.
        using var httpClient = new HttpClient(new TimeoutStubHandler());
        var engine = new OpenAICompatibleAgentEngine(httpClient);

        Func<Task> act = async () => await DrainAsync(engine.CompleteAsync(
            OpenAIFixtureFiles.Request(), new EngineCallOptions(Stream: true), CancellationToken.None));

        var exception = await act.Should().ThrowAsync<EngineCallException>();
        exception.Which.StatusCode.Should().Be(408);
        exception.Which.Detail.Should().Be("engine call timed out");
    }

    [Fact]
    public async Task OpenAICompatibleAgentEngine_CompleteAsync_Stream_WithoutFinishReason_ThrowsEngineCallException()
    {
        // The transport succeeds but no frame ever carries finish_reason: a
        // silent, usage-less completion would look like success to the caller,
        // so the engine surfaces it as a protocol failure instead.
        using var httpClient = new HttpClient(new SseStubHandler(
            "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\n"
            + "data: [DONE]\n\n"));
        var engine = new OpenAICompatibleAgentEngine(httpClient);

        Func<Task> act = async () => await DrainAsync(engine.CompleteAsync(
            OpenAIFixtureFiles.Request(), new EngineCallOptions(Stream: true), CancellationToken.None));

        var exception = await act.Should().ThrowAsync<EngineCallException>();
        exception.Which.StatusCode.Should().Be(200, "the HTTP call itself succeeded");
        exception.Which.Detail.Should().Be("SSE stream ended without a finish_reason frame");
    }

    [Fact]
    public async Task OpenAICompatibleAgentEngine_CompleteAsync_PreCancelledToken_ThrowsOperationCanceledNotEngineCallException()
    {
        // The 408 translation must fire ONLY for transport timeouts: caller
        // cancellation keeps its contract shape (OperationCanceledException,
        // never EngineCallException) — the negative branch of the
        // `when (!ct.IsCancellationRequested)` filter.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var httpClient = new HttpClient(new SseStubHandler("data: [DONE]\n\n"));
        var engine = new OpenAICompatibleAgentEngine(httpClient);

        Exception? caught = null;
        try
        {
            await DrainAsync(engine.CompleteAsync(
                OpenAIFixtureFiles.Request(), new EngineCallOptions(Stream: true), cts.Token));
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        caught.Should().NotBeNull("the pre-cancelled token must abort the call");
        caught.Should().BeAssignableTo<OperationCanceledException>(
            "caller cancellation keeps the documented OperationCanceledException shape");
        caught.Should().NotBeOfType<EngineCallException>(
            "a caller-initiated cancellation must not be reported as a retryable engine error");
    }

    [Fact]
    public async Task OpenAICompatibleAgentEngine_CompleteAsync_DoneSentinel_EndsStreamWithoutDrainingSocket()
    {
        // The provider emits `data: [DONE]` but never closes the response
        // body: the sentinel itself ends the protocol, so the held terminal
        // event must be yielded WITHOUT waiting for socket EOF (M3T1 review
        // WARNING fix). The stub's stream throws if the engine ever reads
        // past the sentinel — a regression would fail this test.
        using var httpClient = new HttpClient(new SentinelThenFailHandler());
        var engine = new OpenAICompatibleAgentEngine(httpClient);

        List<AgentEvent> events = await DrainAsync(engine.CompleteAsync(
            OpenAIFixtureFiles.Request(), new EngineCallOptions(Stream: true), CancellationToken.None));

        events.Should().HaveCount(2, "one text chunk plus the merged terminal event");
        events[0].Should().BeOfType<AgentTextChunk>().Which.Text.Should().Be("hello");
        events[1].Should().BeOfType<AgentDone>();
    }

    [Fact]
    public async Task OpenAICompatibleAgentEngine_CompleteAsync_Stream_HungBody_DeadlineEndsCallWith408()
    {
        // The provider answers 200 + headers and then never sends a byte.
        // HttpClient.Timeout cannot help here (its timer dies at the headers
        // under ResponseHeadersRead — dotnet/runtime#54674), so the engine's
        // OWN per-call deadline must end the stalled body read with 408
        // instead of wedging the caller forever.
        using var httpClient = new HttpClient(new HangingBodyHandler());
        var engine = new OpenAICompatibleAgentEngine(httpClient);

        Func<Task> act = async () => await DrainAsync(engine.CompleteAsync(
            OpenAIFixtureFiles.Request(),
            new EngineCallOptions(Stream: true, Timeout: TimeSpan.FromMilliseconds(100)),
            CancellationToken.None));

        var exception = await act.Should().ThrowAsync<EngineCallException>();
        exception.Which.StatusCode.Should().Be(408, "the per-call deadline bounds the body phase");
        exception.Which.Detail.Should().Be("engine call timed out");
    }

    [Fact]
    public async Task OpenAICompatibleAgentEngine_CompleteAsync_NonStreaming_StalledBody_Reports408()
    {
        // The IAgentEngine default path (probe / test-completion): a body
        // read that dies with a transport-style cancellation while the
        // caller's token is live is a timeout, not a shutdown — it must
        // surface as the typed engine error.
        using var httpClient = new HttpClient(new StalledBodyHandler());
        var engine = new OpenAICompatibleAgentEngine(httpClient);

        Func<Task> act = async () => await DrainAsync(engine.CompleteAsync(
            OpenAIFixtureFiles.Request(),
            EngineCallOptions.Default,
            CancellationToken.None));

        var exception = await act.Should().ThrowAsync<EngineCallException>();
        exception.Which.StatusCode.Should().Be(408);
        exception.Which.Detail.Should().Be("engine call timed out");
    }

    [Fact]
    public void OpenAICompatibleAgentEngine_EmbeddedManifest_DeclaresEngineWithWildcardEgress()
    {
        string manifestPath = Path.Combine(
            FindRepositoryRoot(), "src", "EmailAgent.Connectors.OpenAI", "plugin.json");
        File.Exists(manifestPath).Should().BeTrue($"the plugin manifest ships at {manifestPath}");

        PluginManifest manifest = PluginManifest.FromJsonFile(manifestPath);

        manifest.PluginId.Should().Be("openai-compatible");
        manifest.Version.Should().Be("1.0.0");
        manifest.AssemblyName.Should().Be("EmailAgent.Connectors.OpenAI.dll");
        manifest.TypeName.Should().Be(typeof(OpenAICompatibleAgentEngine).FullName,
            "the manifest's entry type is the class this assembly instantiates");
        manifest.Capabilities.Should().BeEquivalentTo(
            new[] { Capability.HttpEgress }, "http_egress is the only requested capability");
        manifest.HttpAllowlist.Should().BeEquivalentTo(
            new[] { "*" }, "SQ-M3-5: wildcard egress is the v1 default");

        new PluginManifestValidator().Validate(manifest).Ok.Should().BeTrue(
            "the shipped manifest must pass the load-time capability gate");
    }

    /// <summary>Walks up from the test output to the repository root (no hard-coded separators).</summary>
    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EmailAgent.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "EmailAgent.sln could not be located from " + AppContext.BaseDirectory);
    }

    /// <summary>Drains an event stream so async-iterator exceptions surface to the assertion.</summary>
    private static async Task<List<AgentEvent>> DrainAsync(IAsyncEnumerable<AgentEvent> source)
    {
        List<AgentEvent> events = [];
        await foreach (AgentEvent evt in source)
        {
            events.Add(evt);
        }

        return events;
    }

    /// <summary>
    /// Answers every request with one canned SSE body and records the outbound
    /// request (method, Authorization header, body) so the streaming tests can
    /// assert the wire shape too — no socket ever opens (AGENTS.md #5).
    /// </summary>
    private sealed class SseStubHandler : HttpMessageHandler
    {
        private readonly string _sseBody;

        public SseStubHandler(string sseBody) => _sseBody = sseBody;

        /// <summary>The single request this stub answered, or <see langword="null"/> before the first call.</summary>
        public StubRequest? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = new StubRequest(
                request.Method,
                request.Headers.Authorization,
                request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_sseBody, Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    /// <summary>The outbound call facts <see cref="SseStubHandler"/> records.</summary>
    private sealed record StubRequest(
        HttpMethod Method,
        AuthenticationHeaderValue? Authorization,
        string? Body);

    /// <summary>
    /// Simulates a transport that gives up before the caller's token fires
    /// (e.g. an HttpClient.Timeout expiry): throws
    /// <see cref="TaskCanceledException"/> while the passed token stays live.
    /// </summary>
    private sealed class TimeoutStubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new TaskCanceledException("simulated transport timeout");
    }

    /// <summary>
    /// Answers with an SSE body ending in the <c>[DONE]</c> sentinel whose
    /// underlying stream FAILS on any read past those bytes — proving the
    /// engine stops at the sentinel instead of draining to socket EOF.
    /// </summary>
    private sealed class SentinelThenFailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new SentinelThenFailContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"},\"finish_reason\":null}]}\n\n"
                    + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
                    + "data: [DONE]\n\n"),
            });
    }

    /// <summary>
    /// Serves the payload once, then throws if asked for more bytes.
    /// </summary>
    private sealed class SentinelThenFailContent : HttpContent
    {
        private readonly byte[] _payload;

        public SentinelThenFailContent(string payload) => _payload = Encoding.UTF8.GetBytes(payload);

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new SentinelThenFailStream(new MemoryStream(_payload)));

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(_payload).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = _payload.Length;
            return true;
        }
    }

    /// <summary>Reads from the inner stream until it ends, then throws instead of returning 0.</summary>
    private sealed class SentinelThenFailStream : Stream
    {
        private readonly Stream _inner;

        public SentinelThenFailStream(Stream inner) => _inner = inner;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException(
                    "stream read past the [DONE] sentinel — the engine must stop at the sentinel, not drain to EOF");
            }

            return read;
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Answers with 200 + headers and a body stream that blocks until the
    /// per-call deadline fires — the stalled-provider shape no client timeout
    /// covers under <c>ResponseHeadersRead</c>.
    /// </summary>
    private sealed class HangingBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new HangingBodyContent(),
            });
    }

    /// <summary>Body whose stream never yields a byte and honors the cancellation token.</summary>
    private sealed class HangingBodyContent : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new HangingStream());

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            Task.CompletedTask; // never used: the engine reads this response with ResponseHeadersRead

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }
    }

    /// <summary>Reads block forever (per the token); sync reads are refused outright.</summary>
    private sealed class HangingStream : Stream
    {
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0; // unreachable — the delay only ends by cancellation
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("the engine must read the response asynchronously");

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Answers with a body that fails every read/serialization path with a
    /// transport-style cancellation — used to pin the non-streaming 408
    /// translation on the <c>IAgentEngine</c> default path.
    /// </summary>
    private sealed class StalledBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StalledBodyContent(),
            });
    }

    /// <summary>Body that throws <see cref="TaskCanceledException"/> on any consumption path.</summary>
    private sealed class StalledBodyContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            throw new TaskCanceledException("simulated stalled response body");

        protected override Task SerializeToStreamAsync(
            Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken) =>
            throw new TaskCanceledException("simulated stalled response body");

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            throw new TaskCanceledException("simulated stalled response body");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }
    }
}
