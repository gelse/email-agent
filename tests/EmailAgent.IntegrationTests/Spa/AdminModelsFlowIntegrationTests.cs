using System.Net;
using Bunit;
using EmailAgent.Connectors.OpenAI;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Enums;
using EmailAgent.Infrastructure.Plugins;
using EmailAgent.IntegrationTests.Endpoints;
using EmailAgent.IntegrationTests.Fixtures;
using EmailAgent.Spa.Auth;
using EmailAgent.Spa.Models;
using EmailAgent.Spa.Notifications;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

// The models page lives in EmailAgent.Spa.Pages.Admin; it is aliased because
// this assembly's own namespace tree and the typed client's namespace both
// make the bare name ambiguous.
using ModelsPage = EmailAgent.Spa.Pages.Admin.Models;

namespace EmailAgent.IntegrationTests.Spa;

// ---------------------------------------------------------------------------
// M3T5 step 9: the /admin/models screens driven through real bUnit pages over
// the PRODUCTION transport (BearerTokenHandler -> recorded seam -> TestServer)
// against Testcontainers Postgres + the in-process OpenAIMock. One flow, task
// step 9: empty state -> add through the hand-written dialog (the mandatory
// tool-calling probe runs against the mock's probe-pass scenario) -> Pass
// badge + save toast -> test completion against the happy scenario (response,
// tokens, derived cost) -> confirm-delete -> empty state again.
//
// Isolation rules are M3T3/M3T6 parity: one unique `m3t5-<guid>` marker used
// as the model_name AND the cleanup key, audit rows are never deleted
// (append-only HMAC chain), one OpenAIMockFixture per class with the scenario
// pinned per step, and the engine registered through IPluginRegistry only
// AFTER the host has booted (PluginLoader clears the registry on start).
// No socket, no real provider, no literal that is not a test fixture
// (AGENTS.md test constraints).
// ---------------------------------------------------------------------------

[Trait("Category", "M3T5")]
[Collection("Postgres")]
public class AdminModelsFlowIntegrationTests(OpenAIMockFixture fixture, PostgresFixture postgres)
    : IClassFixture<OpenAIMockFixture>, IAsyncLifetime
{
    /// <summary>Throwaway integration-test api_key (never a real credential).</summary>
    private const string ApiKey = "sk-test-redacted-m3t5";

    private const string Provider = "openai-compatible";

    private const string DisplayName = "m3t5 flow model";

    private const string SystemPrompt = "You are helpful.";

    private const string UserPrompt = "Say hello.";

    /// <summary>Argon2-free but container-backed round-trips still need headroom.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>Same-origin dev origin (nginx serves SPA + API — §13.9).</summary>
    private static readonly Uri BaseAddress = new("http://localhost:8081/");

    private string? _marker;

    // -----------------------------------------------------------------------
    // Task step 9: add -> probe-pass -> test completion -> delete.
    // -----------------------------------------------------------------------

    /// <summary>
    /// The whole M3T5 acceptance path through the real API: the empty state
    /// offers "Add model", the dialog's create carries the key exactly once
    /// and never renders it back, the row appears with the Pass badge and the
    /// save toast, the test-completion dialog renders text/tokens/cost, and
    /// the confirm modal removes the row plus its catalog entry.
    /// </summary>
    [Fact]
    public async Task ModelsFlow_Add_ProbePass_TestCompletion_ThenDelete_ThroughRealApi()
    {
        var host = await CreateHostAsync(scenario: "probe-pass");
        await using var factory = host.Factory;

        using var recorder = new RecordingHandler
        {
            InnerHandler = factory.Server.CreateHandler(),
        };
        using var flow = BuildFlow(factory, recorder, host.Bearer);

        var page = flow.RenderComponent<ModelsPage>();
        var toasts = flow.RenderComponent<ToastContainer>();

        // ---- empty state: the screen invites the very first add ----
        page.WaitForAssertion(
            () => page.Find("#models-empty").Should().NotBeNull(
                "a fresh admin owns no models yet"), Wait);
        page.Markup.Should().Contain("No models configured. Click 'Add model' to add one.");
        page.Find("#add-model-open").TextContent.Should().Be("Add model");

        // ---- add through the dialog: the mandatory probe runs on save ----
        page.Find("#add-model-open").Click();
        page.WaitForAssertion(
            () => page.Find("#model-display-name").Should().NotBeNull(
                "the add dialog renders its form as soon as it is opened"), Wait);
        page.Find("#model-display-name").Change(DisplayName);
        page.Find("#model-base-url").Change(fixture.BaseUrl);
        page.Find("#model-model-name").Change(host.Marker);
        page.Find("#model-api-key").Change(ApiKey);
        page.Find("#model-price-input").Change("0.02");
        page.Find("#model-price-output").Change("0.01");
        page.Find("#model-save").Click();

        page.WaitForAssertion(
            () => page.FindAll("#model-display-name").Should().BeEmpty(
                "the parent closes the dialog on OnSaved — the POST answered 201"), Wait);
        page.WaitForAssertion(
            () => page.Markup.Should().Contain(DisplayName,
                "the post-save reload renders the created row"), Wait);
        page.FindAll(".badge-ok").Should().NotBeEmpty(
            "the probe passed, so the row carries the green Pass badge");
        page.Find(".badge-ok").TextContent.Should().BeOneOf("Enabled", "Pass");
        toasts.WaitForAssertion(() => flow.Toasts.Items.Should().ContainSingle(
                t => t.Kind == ToastKind.Success
                    && t.Message == $"Model '{DisplayName}' saved — tool-calling probe: Pass.",
                "the save toast names the probe verdict the API just stored"), Wait);

        var modelId = await SingleModelIdAsync(factory, host.Marker);
        page.WaitForAssertion(
            () => page.FindAll($"#model-edit-{modelId:N}").Should().ContainSingle(), Wait);
        page.Find($"#model-test-{modelId:N}").TextContent.Should().Be("Test completion");
        page.Find($"#model-toggle-{modelId:N}").TextContent.Should().Be("Disable");
        page.Find($"#model-delete-{modelId:N}").TextContent.Should().Be("Delete");

        // ---- the credential travelled once, in the body, and never came back ----
        var create = recorder.Calls.Should().ContainSingle(
                call => call.Method == "POST" && call.Path == "/v1/admin/models")
            .Which;
        create.Body.Should().Contain($"\"api_key\":\"{ApiKey}\"",
            "the create carries the key the form collected (stored as a wrapped blob)");
        create.Body.Should().Contain($"\"model_name\":\"{host.Marker}\"")
            .And.Contain($"\"base_url\":\"{fixture.BaseUrl}\"")
            .And.Contain("\"provider\":\"openai-compatible\"",
                "provider is written by the read-only field (SQ-M3-14)");
        page.Markup.Should().NotContain(ApiKey,
            "the ModelDto projection never echoes a credential into the DOM");

        // ---- test completion against the happy scenario ----
        await fixture.SetScenarioAsync("happy");
        page.Find($"#model-test-{modelId:N}").Click();
        page.WaitForAssertion(
            () => page.Find("#test-user-prompt").Should().NotBeNull(), Wait);
        page.Find("#test-system-prompt").Change(SystemPrompt);
        page.Find("#test-user-prompt").Change(UserPrompt);
        page.Find("#test-run").Click();

        page.WaitForAssertion(
            () => page.Find("#test-response").Should().NotBeNull(
                "a 200 renders the response section inline"), Wait);
        page.Find("#test-response-text").TextContent.Should().Be(
            "hello world", "the mock's recorded completion content");
        var tokens = page.Find("#test-tokens").TextContent;
        tokens.Should().Contain("prompt: 5").And.Contain("completion: 2")
            .And.Contain("total: 7");
        page.Find("#test-cost").TextContent.Should().Contain(
            "0.000120",
            "(5/1000)*0.02 + (2/1000)*0.01 — the prices this dialog sent, six decimals");
        page.Find("#test-duration").TextContent.Should().Contain(
            "Duration (ms):", "the §13.12 duration renders with its label");
        page.Find("#test-close").Click();
        page.WaitForAssertion(
            () => page.FindAll("#test-response").Should().BeEmpty(), Wait);

        // ---- confirm-delete: row gone, empty state back, catalog row removed ----
        page.Find($"#model-delete-{modelId:N}").Click();
        page.Find("#delete-model-title").TextContent.Should().Be("Delete model");
        page.Find("#delete-model-confirm").Click();
        page.WaitForAssertion(() =>
        {
            page.FindAll("#delete-model-title").Should().BeEmpty(
                "the modal closes once the API answers 204");
            page.Find("#models-empty").Should().NotBeNull(
                "the reload after the delete finds no rows");
        }, Wait);
        toasts.WaitForAssertion(() => flow.Toasts.Items.Should().Contain(
                t => t.Kind == ToastKind.Success
                    && t.Message == $"Model '{DisplayName}' deleted."), Wait);

        (await ModelCountAsync(factory, host.Marker)).Should().Be(
            0, "the model_catalog row is gone with its key blob");

        // ---- transport: every models call carried the admin JWT ----
        var modelsCalls = recorder.Calls
            .Where(call => call.Path.StartsWith("/v1/admin/models", StringComparison.Ordinal))
            .ToList();
        modelsCalls.Should().NotBeEmpty();
        modelsCalls.Should().OnlyContain(
            call => call.Authorization == $"Bearer {host.Bearer}",
            "BearerTokenHandler stamps the JWT on every catalog call");
        modelsCalls.Select(call => call.Path.Split('?')[0]).Should().Contain([
            "/v1/admin/models",
            $"/v1/admin/models/{modelId}/test-completion",
            $"/v1/admin/models/{modelId}"
        ]);
    }

    // -----------------------------------------------------------------------
    // Lifecycle (one class instance per test) + per-test cleanup.
    // -----------------------------------------------------------------------

    /// <summary>No per-test setup — <see cref="CreateHostAsync"/> is the setup seam.</summary>
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Per-test cleanup on the SHARED container database: the catalog rows
    /// this test created (marker prefix) and the key blobs they reference.
    /// Audit rows are deliberately left in place — audit_log is append-only
    /// (the HMAC chain spans the shared table) and every audit assertion
    /// correlates by model id.
    /// </summary>
    public async Task DisposeAsync()
    {
        var marker = _marker;
        if (marker is null)
        {
            return;
        }

        await AccountTestHost.MigrateAsync(postgres);
        await using var db = new EmailAgentDbContext(
            new DbContextOptionsBuilder<EmailAgentDbContext>()
                .UseNpgsql(postgres.ConnectionString)
                .Options);

        var rows = await db.ModelCatalog.AsNoTracking()
            .Where(model => model.ModelName.StartsWith(marker))
            .Select(model => new { model.ApiKeySecretRef })
            .ToListAsync();

        var blobIds = new List<Guid>(rows.Count);
        foreach (var row in rows)
        {
            if (row.ApiKeySecretRef is { } reference && Guid.TryParse(reference, out var blobId))
            {
                blobIds.Add(blobId);
            }
        }

        if (blobIds.Count > 0)
        {
            await db.EncryptedBlobs
                .Where(blob => blobIds.Contains(blob.Id))
                .ExecuteDeleteAsync();
        }

        await db.ModelCatalog
            .Where(model => model.ModelName.StartsWith(marker))
            .ExecuteDeleteAsync();
    }

    // -----------------------------------------------------------------------
    // Host + engine seam
    // -----------------------------------------------------------------------

    private sealed record TestHost(
        WebApplicationFactory<Program> Factory,
        string Bearer,
        string Marker);

    /// <summary>
    /// One host per test: migrated shared Postgres, a seeded Admin with a real
    /// issuer-minted token, this class's OpenAIMock pinned to the asked
    /// scenario and a fresh unique marker (model_name + cleanup key).
    /// </summary>
    /// <remarks>
    /// The engine is registered into <b>IPluginRegistry</b> — the seam M3T6
    /// resolves (provider → engine) and the one the catalog probe shares —
    /// and only AFTER the host is booted: <c>CreateClient</c> starts the
    /// hosted services, and PluginLoader.StartAsync awaits a ReloadAsync that
    /// CLEARS the registry (its per-factory plugins directory does not exist,
    /// so it registers nothing itself).
    /// </remarks>
    private async Task<TestHost> CreateHostAsync(string scenario = "probe-pass")
    {
        var marker = $"m3t5-{Guid.NewGuid():N}";
        _marker = marker;

        // Deterministic mock state before any probe can run.
        await fixture.SetScenarioAsync(scenario);

        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        var factory = ApiTestFactory.Create(connectionString);

        // Boots the host (hosted services included) — the only reason this
        // client is created here; the flow below drives the server directly.
        _ = AccountTestHost.CreateClient(factory);

        factory.Services.GetRequiredService<IPluginRegistry>().RegisterAgentEngine(
            Provider, new OpenAICompatibleAgentEngine(fixture.Client));

        var adminId = await AdminTestHost.SeedUserAsync(
            factory, $"{marker}@example.test", UserRole.Admin);
        var bearer = await AdminTestHost.BearerAsync(factory, adminId, "Admin");
        return new TestHost(factory, bearer, marker);
    }

    // -----------------------------------------------------------------------
    // Rig: the production handler chain over the TestServer plus the bUnit
    // context that registers what Program.cs registers.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Bearer outside, recorder inside — the order Program.cs registers its
    /// handlers (no DekHeaderHandler: the M2-only header belongs to the
    /// sources namespace, never to the admin catalog).
    /// </summary>
    private static ModelsFlowContext BuildFlow(
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

        return new ModelsFlowContext(apiHttp, store);
    }

    /// <summary>
    /// bUnit context for the flow: the production HttpClient, the singleton
    /// token store the chain and the pages share, and the Program.cs
    /// registrations the models screens need (toast bus + typed client).
    /// </summary>
    private sealed class ModelsFlowContext : TestContext
    {
        public ModelsFlowContext(HttpClient api, TokenStore store)
        {
            Services.AddSingleton(store);
            Services.AddSingleton(api);
            Services.AddSingleton<JwtRoleParser>();
            Services.AddSingleton<ToastService>();
            Services.AddScoped(sp => new ModelsClient(sp.GetRequiredService<HttpClient>()));
        }

        public ToastService Toasts => Services.GetRequiredService<ToastService>();
    }

    /// <summary>IHttpClientFactory returning the one configured client (refresh path).</summary>
    private sealed class FixedClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    /// <summary>
    /// The recording seam between the SPA's handler chain and the TestServer:
    /// method, path, body and status plus the Authorization header captured
    /// after BearerTokenHandler stamped the request and before the server saw
    /// it.
    /// </summary>
    private sealed class RecordingHandler : DelegatingHandler
    {
        public sealed record RecordedCall(
            string Method,
            string Path,
            string? Body,
            HttpStatusCode Status,
            string? Authorization);

        private readonly List<RecordedCall> _calls = [];

        public IReadOnlyList<RecordedCall> Calls
        {
            get
            {
                lock (_calls)
                {
                    return [.. _calls];
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

    // -----------------------------------------------------------------------
    // Database helpers
    // -----------------------------------------------------------------------

    private static async Task<Guid> SingleModelIdAsync(
        WebApplicationFactory<Program> factory, string marker)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var model = await db.ModelCatalog.AsNoTracking()
            .SingleAsync(row => row.ModelName == marker);
        return model.Id;
    }

    private static async Task<int> ModelCountAsync(
        WebApplicationFactory<Program> factory, string marker)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return await db.ModelCatalog.AsNoTracking()
            .CountAsync(row => row.ModelName == marker);
    }
}
