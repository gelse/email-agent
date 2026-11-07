using System.Net;
using Bunit;
using EmailAgent.IntegrationTests.Endpoints;
using EmailAgent.Infrastructure.Enums;
using EmailAgent.Spa.Auth;
using EmailAgent.Spa.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

// The stub page lives in EmailAgent.Spa.Pages.User; it is aliased so the
// namespace import below can never shadow anything else in this file.
using AgentsNewStub = EmailAgent.Spa.Pages.User.AgentsNewStub;

namespace EmailAgent.IntegrationTests.M3EndToEnd;

// ---------------------------------------------------------------------------
// M3T7 step 3: the /user/agents/new STUB picker driven through real bUnit over
// the PRODUCTION transport (BearerTokenHandler -> recorded seam -> TestServer)
// against this collection's Testcontainers Postgres. This is the satisfaction
// of M3 exit criterion 1's second half — "a user can select it in a stub agent
// config" — so the fact does not stop at rendering: it changes the <select>
// and asserts the choice lands in the URL hash (#model=<id>).
//
// Task step 3 seeds exactly two rows through the DbContext: one
// enabled=true/Pass, one enabled=false/Fail. The server-side allow-list is
// what the assertion measures — the picker renders ONE option, never two, and
// the SPA never filters for itself.
// ---------------------------------------------------------------------------

[Trait("Category", "M3EndToEnd")]
[Collection(M3E2ECollection.Name)]
public sealed class StubAgentPageTests : IAsyncLifetime
{
    /// <summary>Argon2-free reads still run over a container — keep headroom.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>Same-origin dev origin (nginx serves SPA + API — §13.9).</summary>
    private static readonly Uri BaseAddress = new("http://localhost:8081/");

    private readonly PostgresFixture _postgres;
    private readonly List<string> _markers = new();

    public StubAgentPageTests(PostgresFixture postgres) => _postgres = postgres;

    /// <summary>
    /// The M3 exit-criterion fact: an ordinary <c>User</c> reaches the stub,
    /// the <c>&lt;select&gt;</c> offers EXACTLY the pass+enabled row (the
    /// disabled+Fail twin the task seeds alongside it is filtered out
    /// server-side), there is no save button, and picking the model writes
    /// <c>#model=&lt;id&gt;</c> into the URL hash for M5/M6a to pick up.
    /// </summary>
    [Fact]
    public async Task User_StubAgentPage_ModelPicker_FiltersToPassEnabled()
    {
        var marker = M3TestHarness.NewMarker();
        _markers.Add(marker);
        var host = await M3TestHarness.CreateHostAsync(_postgres, marker);
        await using var factory = host.Factory;

        var passingId = await M3TestHarness.SeedRowAsync(
            factory, host.AdminId, $"{marker} passing model", $"{marker}-pass",
            enabled: true, probeResult: ToolCallingProbeResult.Pass);
        var rejectedId = await M3TestHarness.SeedRowAsync(
            factory, host.AdminId, $"{marker} rejected model", $"{marker}-rejected",
            enabled: false, probeResult: ToolCallingProbeResult.Fail);

        // The caller is an ORDINARY User, not an Admin — this is the
        // user-side page and the user-side route role.
        var userId = await AdminTestHost.SeedUserAsync(
            factory, $"{marker}-user@example.test", UserRole.User);
        var bearer = await AdminTestHost.BearerAsync(factory, userId, "User");

        using var recorder = new RecordingHandler
        {
            InnerHandler = factory.Server.CreateHandler(),
        };
        using var flow = BuildFlow(factory, recorder, bearer);
        flow.Nav.NavigateTo("/user/agents/new");

        var page = flow.RenderComponent<AgentsNewStub>();
        page.WaitForAssertion(
            () => page.Find("#agent-model-picker").Should().NotBeNull(
                "the picker renders as soon as the allow-list loads"), Wait);

        // Task step 3: exactly one option for the passing row. The <select>
        // also carries its empty placeholder, which is not a model — the
        // value-bearing options are what the allow-list produced.
        var modelOptions = page.FindAll("#agent-model-picker option")
            .Where(option => !string.IsNullOrWhiteSpace(option.GetAttribute("value")))
            .ToList();
        modelOptions.Should().ContainSingle(
            "only the enabled AND Pass row survives the server-side allow-list");
        modelOptions[0].GetAttribute("value").Should().Be(
            passingId.ToString(), "the option's value IS the model id");
        modelOptions[0].TextContent.Should().Be($"{marker} passing model");
        page.Markup.Should().NotContain(
            $"{marker} rejected model", "a disabled/failed row must never reach the picker");

        // No save action: the stub only acknowledges (task scope Out bullet).
        page.FindAll("button").Should().BeEmpty(
            "the stub ships no save button — PUT /v1/user/sources/{id}/agent lands in M5/M6a");
        page.Find("#agent-new-no-save").TextContent.Should().Be(
            "Save action lands in M5. The selected model id is in the URL hash for future use.");
        page.FindAll("#agent-new-error").Should().BeEmpty();
        page.FindAll("#agent-new-empty").Should().BeEmpty(
            "an allow-list of one is not the empty state");

        // Exit criterion 1: the user can SELECT it in the stub config, and the
        // selection is acknowledged in the URL hash for the future CRUD.
        page.Find("#agent-model-picker").Change(passingId.ToString());
        flow.Nav.Uri.Should().Be(
            $"http://localhost/user/agents/new#model={passingId}",
            "the pick is acknowledged client-side (forceLoad: false), no reload");

        // Transport: one read, limit=100 (task step 2 of the stub), stamped
        // with the ordinary User's JWT — never an admin route.
        var listCalls = recorder.Calls
            .Where(call => call.Path.StartsWith("/v1/user/models", StringComparison.Ordinal))
            .ToList();
        listCalls.Should().ContainSingle("the page loads the allow-list exactly once");
        listCalls[0].Path.Should().Be("/v1/user/models?limit=100");
        listCalls[0].Authorization.Should().Be(
            $"Bearer {bearer}", "BearerTokenHandler stamps the JWT on the picker read");
        listCalls[0].Status.Should().Be(HttpStatusCode.OK);
        listCalls.Should().OnlyContain(
            call => !call.Path.Contains("/v1/admin/", StringComparison.Ordinal),
            "the user-side page never touches an admin namespace");
    }

    // -----------------------------------------------------------------------
    // Lifecycle (one class instance per fact) + per-fact cleanup
    // -----------------------------------------------------------------------

    /// <summary>No per-test setup — the seeding above is the setup seam.</summary>
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Blobs first, then the marker-keyed catalog rows. Audit rows are never touched.</summary>
    public async Task DisposeAsync()
    {
        foreach (var marker in _markers)
        {
            await M3TestHarness.CleanupAsync(_postgres, marker);
        }
    }

    // -----------------------------------------------------------------------
    // Rig: the production handler chain over the TestServer plus the bUnit
    // context that registers what Program.cs registers for this page.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Bearer outside, recorder inside — the order Program.cs registers its
    /// handlers (no DekHeaderHandler: the sources-only header has no business
    /// on a model read).
    /// </summary>
    private static StubFlowContext BuildFlow(
        WebApplicationFactory<Program> factory,
        RecordingHandler recorder,
        string bearer)
    {
        var store = new TokenStore();
        store.Set(bearer);

        var refreshHttp = new HttpClient(factory.Server.CreateHandler())
        {
            BaseAddress = BaseAddress,
        };
        var authenticator = new RefreshCookieAuthenticator(
            new FixedClientFactory(refreshHttp), store);

        var apiHttp = new HttpClient(
            new BearerTokenHandler(store, authenticator)
            {
                InnerHandler = recorder,
            })
        {
            BaseAddress = BaseAddress,
        };

        return new StubFlowContext(apiHttp, store);
    }

    /// <summary>
    /// bUnit context for the flow: the production HttpClient, the singleton
    /// token store the chain and the page share, and the Program.cs
    /// registrations the stub page needs (role parser + typed client).
    /// </summary>
    private sealed class StubFlowContext : TestContext
    {
        public StubFlowContext(HttpClient api, TokenStore store)
        {
            Services.AddSingleton(store);
            Services.AddSingleton(api);
            Services.AddSingleton<JwtRoleParser>();
            Services.AddScoped(sp => new UserModelsClient(
                sp.GetRequiredService<HttpClient>()));
        }

        /// <summary>bUnit's fake NavigationManager (records NavigateTo targets).</summary>
        public Microsoft.AspNetCore.Components.NavigationManager Nav
            => Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
    }

    /// <summary>IHttpClientFactory returning the one configured client (refresh path).</summary>
    private sealed class FixedClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    /// <summary>
    /// The recording seam between the SPA's handler chain and the TestServer:
    /// method, path, status plus the Authorization header captured after
    /// BearerTokenHandler stamped the request and before the server saw it.
    /// </summary>
    private sealed class RecordingHandler : DelegatingHandler
    {
        public sealed record RecordedCall(
            string Method,
            string Path,
            string? Body,
            HttpStatusCode Status,
            string? Authorization);

        private readonly List<RecordedCall> _calls = new();

        public IReadOnlyList<RecordedCall> Calls
        {
            get
            {
                lock (_calls)
                {
                    return _calls.ToArray();
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var authorization = request.Headers.Authorization?.ToString();
            var path = request.RequestUri?.PathAndQuery ?? "/";

            var response = await base.SendAsync(request, cancellationToken);

            lock (_calls)
            {
                _calls.Add(new RecordedCall(
                    request.Method.Method,
                    path,
                    body,
                    response.StatusCode,
                    authorization));
            }

            return response;
        }
    }
}
