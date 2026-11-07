using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmailAgent.IntegrationTests.Fixtures;

/// <summary>
/// M3T1 (SQ-M3-7/8): brings up the in-process <c>OpenAIMock</c> host through
/// <c>WebApplicationFactory&lt;OpenAIMock.Program&gt;</c> for one test class —
/// no socket, no container, no real provider (AGENTS.md hard constraint #5).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BaseUrl"/> is the OpenAI-compatible <c>base_url</c> to put into
/// the engine configuration under test (<c>{server}/v1</c>); <see cref="Client"/>
/// is the TestServer's in-memory client — handing it to the engine is what
/// keeps the call off the network even though the engine uses absolute URIs.
/// </para>
/// <para>
/// <see cref="SetScenarioAsync"/> steers the mock's ACTIVE scenario through its
/// test-only control endpoint, which is how tests driving the engine (it sends
/// no custom headers) select a response shape; <see cref="RequestCounts"/>
/// exposes the per-scenario counters for cache/relay assertions and
/// <see cref="LastRequestBody"/> the most recently received request body for
/// wire-shape assertions. Two fixture
/// instances are two independent mock hosts with independent state.
/// </para>
/// </remarks>
public sealed class OpenAIMockFixture : IAsyncLifetime
{
    private WebApplicationFactory<OpenAIMock.Program>? _factory;

    /// <summary>In-memory client bound to the mock's server (the only transport the engine under test may use).</summary>
    public HttpClient Client { get; private set; } = null!;

    /// <summary>Engine-config <c>base_url</c>: the mock's server address plus <c>/v1</c>, no trailing slash.</summary>
    public string BaseUrl { get; private set; } = null!;

    /// <summary>Snapshot of the mock's per-scenario request counters.</summary>
    public IReadOnlyDictionary<string, int> RequestCounts =>
        Services.GetRequiredService<OpenAIMock.MockState>().RequestCounts;

    /// <summary>
    /// The raw body of the mock's most recently received chat-completions
    /// request — the wire shape tests can assert on (M3T2's probe carrying
    /// <c>max_tokens: 1</c> per SQ-M3-3 / Q-L15); <see langword="null"/> until
    /// the first call.
    /// </summary>
    public string? LastRequestBody =>
        Services.GetRequiredService<OpenAIMock.MockState>().LastRequestBody;

    private IServiceProvider Services =>
        (_factory ?? throw new InvalidOperationException(
            "OpenAIMockFixture was not initialized — InitializeAsync must run first.")).Services;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<OpenAIMock.Program>();
        Client = _factory.Server.CreateClient();
        BaseUrl = new Uri(_factory.Server.BaseAddress!, "v1").AbsoluteUri.TrimEnd('/');

        // Readiness + deterministic start: the control round-trip proves the
        // host is serving before any test runs and pins the default scenario.
        await SetScenarioAsync("happy");
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        Client.Dispose();
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Switches the mock's active scenario via its test-only control endpoint.</summary>
    /// <param name="scenario">One of <c>happy</c>, <c>probe-pass</c>, <c>probe-fail</c>, <c>error</c>.</param>
    public async Task SetScenarioAsync(string scenario)
    {
        using var content = new StringContent(scenario);
        using var response = await Client.PostAsync(OpenAIMock.Program.ControlScenarioPath, content);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Engine configuration JSON pointing at this fixture's mock (test key only — never a real credential).</summary>
    public JsonElement CreateConfig(string modelName = "test-model")
    {
        var json = JsonSerializer.Serialize(new
        {
            base_url = BaseUrl,
            api_key = "sk-test-redacted",
            model_name = modelName,
        });
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
