using System.Collections.Concurrent;
using System.Text.Json;

namespace OpenAIMock;

/// <summary>
/// M3T1 (SQ-M3-7): a hand-rolled OpenAI-compatible mock —
/// <c>POST /v1/chat/completions</c> answering either one JSON body
/// (<c>stream=false</c>) or an SSE frame sequence (<c>stream=true</c>), plus
/// the test-only control endpoint <c>POST /__test/scenario</c>. Started
/// in-process through <c>WebApplicationFactory&lt;OpenAIMock.Program&gt;</c> —
/// never a socket, never a real provider (AGENTS.md hard constraint #5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scenario resolution:</b> the <c>X-Test-Scenario</c> request header wins
/// when present (M3T2's probe sends it); otherwise the instance's active
/// scenario applies — initialized from <c>--scenario &lt;name&gt;</c> /
/// env <c>OPENAIMOCK__SCENARIO</c> (default <c>happy</c>) and switchable at
/// runtime via <c>POST /__test/scenario</c>, which
/// <c>OpenAIMockFixture.SetScenario</c> wraps. Engine-driven calls carry no
/// custom header (the engine sends only <c>Authorization: Bearer …</c>), so
/// those tests steer the response shape through the active scenario.
/// </para>
/// <para>
/// <b>Fixtures:</b> the non-streamed bodies are the committed recordings under
/// <c>tests/fixtures/openai/</c> (located by walking up from the test output —
/// no hard-coded path separators, portable per AGENTS.md); the streamed frames
/// are the same payloads written inline with ~10 ms between frames so
/// cancellation-mid-stream and duration assertions stay deterministic.
/// Every fixture carries the literal <c>sk-test-redacted</c> key marker —
/// never a real credential.
/// </para>
/// </remarks>
public class Program
{
    /// <summary>Header a test (or the M3T2 probe) uses to pick the scenario per request.</summary>
    public const string ScenarioHeader = "X-Test-Scenario";

    /// <summary>Test-only control endpoint switching the instance's active scenario.</summary>
    public const string ControlScenarioPath = "/__test/scenario";

    /// <summary>The OpenAI-compatible chat-completions route.</summary>
    public const string ChatCompletionsPath = "/v1/chat/completions";

    /// <summary>Delay between streamed frames — deterministic and long enough for mid-stream assertions.</summary>
    private const int FrameDelayMs = 10;

    private const string HappyFrame1 =
        """{"id":"cmpl-live-1","object":"chat.completion.chunk","created":1712000000,"model":"test-model","choices":[{"index":0,"delta":{"role":"assistant","content":"hello"},"finish_reason":null}]}""";

    private const string HappyFrame2 =
        """{"id":"cmpl-live-1","object":"chat.completion.chunk","created":1712000000,"model":"test-model","choices":[{"index":0,"delta":{"content":" world"},"finish_reason":null}]}""";

    private const string HappyFrame3 =
        """{"id":"cmpl-live-1","object":"chat.completion.chunk","created":1712000000,"model":"test-model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":2,"total_tokens":7}}""";

    private const string ProbePassFrame1 =
        """{"id":"cmpl-live-1","object":"chat.completion.chunk","created":1712000000,"model":"test-model","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"function":{"name":"ping","arguments":"{}"}}]},"finish_reason":null}]}""";

    private const string ProbePassFrame2 =
        """{"id":"cmpl-live-1","object":"chat.completion.chunk","created":1712000000,"model":"test-model","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":4,"completion_tokens":6,"total_tokens":10}}""";

    private const string ProbeFailFrame1 =
        """{"id":"cmpl-live-1","object":"chat.completion.chunk","created":1712000000,"model":"test-model","choices":[{"index":0,"delta":{"role":"assistant","content":"I can't do tool calls"},"finish_reason":null}]}""";

    private const string ProbeFailFrame2 =
        """{"id":"cmpl-live-1","object":"chat.completion.chunk","created":1712000000,"model":"test-model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":4,"completion_tokens":9,"total_tokens":13}}""";

    /// <summary>Entry point — an explicit Main (never top-level statements) so this
    /// <c>Program</c> can coexist with EmailAgent.Api's in the test output.</summary>
    public static void Main(string[]? args)
    {
        var state = new MockState(
            ResolveInitialScenario(args),
            ResolveFixtureDirectory());

        var builder = WebApplication.CreateBuilder(args ?? Array.Empty<string>());
        builder.Services.AddSingleton(state);

        var app = builder.Build();

        app.MapPost(
            ControlScenarioPath,
            async (HttpContext context) =>
            {
                using var reader = new StreamReader(context.Request.Body);
                var scenario = (await reader.ReadToEndAsync()).Trim();
                if (scenario.Length == 0)
                {
                    return Results.BadRequest(new
                    {
                        error = new { message = "scenario name is required", type = "invalid_request_error" },
                    });
                }

                state.SetScenario(scenario);
                return Results.Ok(new { scenario });
            });

        app.MapPost(
            ChatCompletionsPath,
            async (HttpContext context) => await HandleCompletionAsync(context, state));

        app.Run();
    }

    /// <summary>One chat-completions call: scenario → counter → streamed frames or recorded body.</summary>
    private static async Task HandleCompletionAsync(HttpContext context, MockState state)
    {
        string requestBody;
        using (var reader = new StreamReader(context.Request.Body))
        {
            requestBody = await reader.ReadToEndAsync();
        }

        bool stream;
        try
        {
            using var document = JsonDocument.Parse(requestBody);
            stream = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("stream", out var streamElement)
                && streamElement.ValueKind == JsonValueKind.True;
        }
        catch (JsonException ex)
        {
            await WriteJsonAsync(
                context,
                StatusCodes.Status400BadRequest,
                JsonSerializer.Serialize(new
                {
                    error = new { message = $"request body is not valid JSON: {ex.Message}", type = "invalid_request_error" },
                }));
            return;
        }

        var requestedScenario = context.Request.Headers[ScenarioHeader].FirstOrDefault();
        var scenario = string.IsNullOrWhiteSpace(requestedScenario) ? state.ActiveScenario : requestedScenario!;
        state.RecordRequest(scenario, requestBody);

        if (string.Equals(scenario, "error", StringComparison.Ordinal))
        {
            await WriteJsonAsync(
                context, StatusCodes.Status401Unauthorized, state.ReadFixture("error-bad-request.json"));
            return;
        }

        var frames = StreamingFramesFor(scenario);
        if (frames is null)
        {
            await WriteJsonAsync(
                context,
                StatusCodes.Status400BadRequest,
                JsonSerializer.Serialize(new
                {
                    error = new { message = $"unknown scenario '{scenario}'", type = "invalid_request_error" },
                }));
            return;
        }

        if (!stream)
        {
            await WriteJsonAsync(context, StatusCodes.Status200OK, state.ReadFixture(NonStreamingFixtureFor(scenario)));
            return;
        }

        context.Response.ContentType = "text/event-stream";
        try
        {
            foreach (var frame in frames)
            {
                await context.Response.WriteAsync($"data: {frame}\n\n");
                await context.Response.Body.FlushAsync();
                await Task.Delay(FrameDelayMs);
            }

            await context.Response.WriteAsync("data: [DONE]\n\n");
            await context.Response.Body.FlushAsync();
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // The client went away mid-stream (the cancellation test does) —
            // nothing to answer anymore; the client's own exception is what
            // the test asserts on.
        }
    }

    /// <summary>Streamed frames for a known scenario, or <c>null</c> when the scenario is unknown.</summary>
    private static IReadOnlyList<string>? StreamingFramesFor(string scenario) =>
        scenario switch
        {
            "happy" => new[] { HappyFrame1, HappyFrame2, HappyFrame3 },
            "probe-pass" => new[] { ProbePassFrame1, ProbePassFrame2 },
            "probe-fail" => new[] { ProbeFailFrame1, ProbeFailFrame2 },
            _ => null,
        };

    /// <summary>The committed recording a non-streamed call replays for a known scenario.</summary>
    private static string NonStreamingFixtureFor(string scenario) =>
        scenario switch
        {
            "happy" => "happy-non-streaming.json",
            "probe-pass" => "probe-pass.json",
            "probe-fail" => "probe-fail.json",
            _ => throw new InvalidOperationException($"scenario '{scenario}' has no non-streaming fixture"),
        };

    private static async Task WriteJsonAsync(HttpContext context, int statusCode, string json)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(json);
    }

    /// <summary><c>--scenario &lt;name&gt;</c> wins, then env <c>OPENAIMOCK__SCENARIO</c>, else <c>happy</c>.</summary>
    private static string ResolveInitialScenario(string[]? args)
    {
        if (args is not null)
        {
            for (var i = 0; i + 1 < args.Length; i++)
            {
                if (string.Equals(args[i], "--scenario", StringComparison.Ordinal))
                {
                    return args[i + 1];
                }
            }
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("OPENAIMOCK__SCENARIO");
        return string.IsNullOrWhiteSpace(fromEnvironment) ? "happy" : fromEnvironment;
    }

    /// <summary>
    /// The committed fixture directory — the first ancestor of the test output
    /// that holds <c>tests/fixtures/openai</c> (the repo-root walk pattern of
    /// <c>SpaBundleSecretTests.FindRepositoryRoot</c>).
    /// </summary>
    private static string ResolveFixtureDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "fixtures", "openai");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"no tests/fixtures/openai directory above {AppContext.BaseDirectory} — run the tests from the repository checkout");
    }
}

/// <summary>
/// The mock's mutable state: the active scenario, a per-scenario request
/// counter and the last received request body (<c>OpenAIMockFixture.RequestCounts</c>
/// / <c>OpenAIMockFixture.LastRequestBody</c> read them from the host's DI).
/// </summary>
public sealed class MockState
{
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<string, int> _requestCounts = new(StringComparer.Ordinal);
    private string _activeScenario;
    private string? _lastRequestBody;

    /// <summary>Creates the state around a fixture directory and an initial scenario.</summary>
    public MockState(string activeScenario, string fixtureDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activeScenario);
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureDirectory);
        _activeScenario = activeScenario;
        FixtureDirectory = fixtureDirectory;
    }

    /// <summary>Directory the committed recordings were resolved to at host start.</summary>
    public string FixtureDirectory { get; }

    /// <summary>The instance's active scenario (what engine-driven calls get, absent the header).</summary>
    public string ActiveScenario
    {
        get
        {
            lock (_sync)
            {
                return _activeScenario;
            }
        }
    }

    /// <summary>Switches the active scenario (the <c>POST /__test/scenario</c> control endpoint).</summary>
    public void SetScenario(string scenario)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario);
        lock (_sync)
        {
            _activeScenario = scenario;
        }
    }

    /// <summary>
    /// Counts one call against <paramref name="scenario"/> and remembers the
    /// raw request body for <see cref="LastRequestBody"/>; returns the new count.
    /// </summary>
    public int RecordRequest(string scenario, string requestBody)
    {
        lock (_sync)
        {
            _lastRequestBody = requestBody;
        }

        return _requestCounts.AddOrUpdate(scenario, 1, (_, current) => current + 1);
    }

    /// <summary>Snapshot of the per-scenario request counters.</summary>
    public IReadOnlyDictionary<string, int> RequestCounts =>
        _requestCounts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    /// <summary>
    /// The raw body of the most recently received chat-completions request
    /// (test-only wire-shape assertions — e.g. M3T2's probe transmitting
    /// <c>max_tokens: 1</c> per SQ-M3-3 / Q-L15); <see langword="null"/> until
    /// the first call.
    /// </summary>
    public string? LastRequestBody
    {
        get
        {
            lock (_sync)
            {
                return _lastRequestBody;
            }
        }
    }

    /// <summary>Reads a committed fixture body from <see cref="FixtureDirectory"/>.</summary>
    public string ReadFixture(string fileName) =>
        File.ReadAllText(Path.Combine(FixtureDirectory, fileName));
}
