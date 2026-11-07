using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmailAgent.Api.Models;
using EmailAgent.Connectors.OpenAI;
using EmailAgent.Core.Audit;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using EmailAgent.Infrastructure.Plugins;
using EmailAgent.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmailAgent.IntegrationTests.Endpoints;

// ---------------------------------------------------------------------------
// M3T6: POST /v1/admin/models/{id}/test-completion against Testcontainers
// Postgres + the real host, with the M3T1 engine driving the in-process
// OpenAIMock through the IPluginRegistry seam — no socket, no real provider
// (AGENTS.md test constraint; the real dev LLM endpoint never appears).
//
// Isolation rules (M3T3 parity):
//  - one unique `m3t6-<guid>` marker per test: the model_name the DisposeAsync
//    cleanup keys its catalog-row/blob deletion on;
//  - audit rows are NEVER deleted (append-only HMAC chain over the shared
//    table) — every audit assertion correlates by model id;
//  - one OpenAIMockFixture per class: SetScenarioAsync pins the active
//    scenario per test (the TestServer client answers ANY host, so the seeded
//    base_url lands on this fixture's active scenario);
//  - each test builds its OWN host, so the 60/min authenticated budget and
//    the plugin registry start fresh per test.
// ---------------------------------------------------------------------------

[Trait("Category", "M3T6")]
[Collection("Postgres")]
public class AdminTestCompletionTests : IClassFixture<OpenAIMockFixture>, IAsyncLifetime
{
    /// <summary>Throwaway integration-test api_key (never a real credential).</summary>
    private const string ApiKey = "sk-test-redacted-m3t6";

    private const string Provider = "openai-compatible";

    private const string SystemPrompt = "You are helpful.";
    private const string UserPrompt = "Say hello.";

    /// <summary>The fixed §13.12 200 projection (snake_case wire names).</summary>
    private static readonly string[] ResponseMembers =
    [
        "response_text",
        "tool_calls",
        "prompt_tokens",
        "completion_tokens",
        "total_tokens",
        "cost_usd",
        "duration_ms",
    ];

    private readonly OpenAIMockFixture _fixture;
    private readonly PostgresFixture _postgres;
    private string? _marker;

    public AdminTestCompletionTests(OpenAIMockFixture fixture, PostgresFixture postgres)
    {
        _fixture = fixture;
        _postgres = postgres;
    }

    // -----------------------------------------------------------------------
    // Happy path
    // -----------------------------------------------------------------------

    /// <summary>Task row: the prompt round-trips and the response projection carries tokens + cost.</summary>
    [Fact]
    public async Task HappyPath_RoundTripsPrompt_AndReturnsTokens()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;
        var requestsBefore = TotalRequests();
        var modelId = await SeedModelAsync(host);

        using var response = await SendAsync(host, modelId);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "a completed call is §13.12 200");
        response.Content.Headers.ContentType?.ToString().Should().StartWith(
            "application/json", "the success body is the DTO, not a problem document");
        TotalRequests().Should().Be(requestsBefore + 1, "exactly one engine call ran");

        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain(ApiKey, "the API key must never reach a response body");
        raw.Should().NotContain("api_key", "the §13.12 surface has no credential member");

        using var document = JsonDocument.Parse(raw);
        var body = document.RootElement.Clone();
        body.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            ResponseMembers, "the fixed 7-member response projection (task scope table)");

        body.GetProperty("response_text").GetString().Should().Be(
            "hello world", "the mock's recorded completion content");
        body.GetProperty("tool_calls").ValueKind.Should().Be(
            JsonValueKind.Null, "no AgentToolCall event → null tool_calls");
        body.GetProperty("prompt_tokens").GetInt32().Should().Be(5);
        body.GetProperty("completion_tokens").GetInt32().Should().Be(2);
        body.GetProperty("total_tokens").GetInt32().Should().Be(7);
        body.GetProperty("cost_usd").GetDecimal().Should().Be(
            0.00012m, "(5/1000)*0.02 + (2/1000)*0.01 — the seeded prices (task acceptance)");
        body.GetProperty("duration_ms").GetInt64().Should().BeGreaterThanOrEqualTo(0);

        // Round trip: the mock received both prompts (SQ-M3-9 markers split by
        // the request builder) and never the key.
        var sent = _fixture.LastRequestBody;
        sent.Should().NotBeNull("the engine posted to the in-process mock");
        sent!.Should().Contain(SystemPrompt, "system_prompt reaches the provider");
        sent.Should().Contain(UserPrompt, "user_prompt reaches the provider");
        sent.Should().Contain("\"stream\":false", "M3T6 calls the interface default — non-streaming");
        sent.Should().NotContain(ApiKey, "the key rides the Authorization header, never the body");
    }

    /// <summary>Task row: the test_model_completion audit row carries the SQ-M3-4 field set.</summary>
    [Fact]
    public async Task HappyPath_AuditRow_ContainsTokensAndPromptHash()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;
        var modelId = await SeedModelAsync(host);

        using var response = await SendAsync(host, modelId);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var row = await SingleAuditAsync(factory, AuditActions.TestModelCompletion, modelId);
        row.TargetType.Should().Be("model_catalog");
        row.ActorUserId.Should().Be(host.AdminId);
        row.ActorRole.Should().Be("Admin");

        using var metadata = JsonDocument.Parse(row.MetadataJson!);
        var root = metadata.RootElement;
        root.GetProperty("model_id").GetGuid().Should().Be(modelId);
        root.GetProperty("actor_user_id").GetGuid().Should().Be(host.AdminId);
        root.GetProperty("duration_ms").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        root.GetProperty("prompt_tokens").GetInt32().Should().Be(5);
        root.GetProperty("completion_tokens").GetInt32().Should().Be(2);
        root.GetProperty("total_tokens").GetInt32().Should().Be(7);
        root.GetProperty("cost_usd").GetDecimal().Should().Be(0.00012m);
        root.GetProperty("tool_call_count").GetInt32().Should().Be(0);
        root.GetProperty("prompt_sha256").GetString().Should().Be(
            ExpectedPromptHash(SystemPrompt, UserPrompt),
            "SQ-M3-4: SHA-256(system_prompt || \"\\n\" || user_prompt), lowercase hex");
        root.GetProperty("model_name").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("base_url").GetString().Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>Task row: the audit row never stores the prompt text itself.</summary>
    [Fact]
    public async Task HappyPath_AuditRow_DoesNotContainPromptText()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;
        var modelId = await SeedModelAsync(host);
        const string secretSystem = "SECRET-SYSTEM-PROMPT-m3t6";
        const string secretUser = "SECRET-USER-PROMPT-m3t6";

        using var response = await SendAsync(
            host, modelId, new { system_prompt = secretSystem, user_prompt = secretUser });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var row = await SingleAuditAsync(factory, AuditActions.TestModelCompletion, modelId);
        row.MetadataJson.Should().NotContain(secretSystem, "the prompt may hold user-style data");
        row.MetadataJson.Should().NotContain(secretUser, "the prompt may hold user-style data");
        row.MetadataJson.Should().NotContain("system_prompt");
        row.MetadataJson.Should().NotContain("user_prompt");
        row.MetadataJson.Should().Contain(ExpectedPromptHash(secretSystem, secretUser));
    }

    // -----------------------------------------------------------------------
    // Gates and failures
    // -----------------------------------------------------------------------

    /// <summary>Task row: an unknown model id is §13.12 404 /errors/model-not-found.</summary>
    [Fact]
    public async Task ModelNotFound_Returns404()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;
        var requestsBefore = TotalRequests();

        using var response = await SendAsync(host, Guid.NewGuid());

        var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.NotFound);
        problem.GetProperty("type").GetString().Should().Be("/errors/model-not-found");
        TotalRequests().Should().Be(requestsBefore, "no model → no engine call");
        AssertRateLimitHeaders(response, "404 problem document");
    }

    /// <summary>Task row: a disabled model is §13.12 400 /errors/model-disabled — never called.</summary>
    [Fact]
    public async Task DisabledModel_Returns400_ModelDisabled()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;
        var requestsBefore = TotalRequests();
        var modelId = await SeedModelAsync(host, enabled: false);

        using var response = await SendAsync(host, modelId);

        var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.BadRequest);
        problem.GetProperty("type").GetString().Should().Be("/errors/model-disabled");
        TotalRequests().Should().Be(requestsBefore, "the disabled gate runs before any engine call");
        (await AuditRowsAsync(factory, modelId)).Should().BeEmpty(
            "a refused test completion writes no test_model_completion row");
    }

    /// <summary>Review fix: a dangling api_key_secret_ref → §13.12 500 /errors/internal-blob-missing, never a generic 500.</summary>
    [Fact]
    public async Task DanglingKeyBlob_Returns500_InternalBlobMissing()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;
        var requestsBefore = TotalRequests();
        var modelId = await SeedModelAsync(host);

        // Break the reference the seed just wrote: the row keeps its GUID
        // pointer, the blob row behind it disappears (nothing left behind).
        using (var scope = host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
            var entry = await db.ModelCatalog.SingleAsync(row => row.Id == modelId);
            var blobId = Guid.Parse(entry.ApiKeySecretRef!);
            db.EncryptedBlobs.Remove(
                await db.EncryptedBlobs.SingleAsync(blob => blob.Id == blobId));
            await db.SaveChangesAsync();
        }

        using var response = await SendAsync(host, modelId);

        var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.InternalServerError);
        problem.GetProperty("type").GetString().Should().Be("/errors/internal-blob-missing");
        TotalRequests().Should().Be(requestsBefore, "no readable key → no engine call");
        (await AuditRowsAsync(factory, modelId)).Should().BeEmpty(
            "a refused test completion writes no test_model_completion row");
    }

    /// <summary>Task row: an engine failure is 500 with an SQ-M3-15 sanitized, clamped detail.</summary>
    [Fact]
    public async Task EngineCallFailure_Returns500_WithSanitizedDetail()
    {
        var host = await CreateHostAsync(scenario: "error-key-echo");
        await using var factory = host.Factory;
        using var client = host.Client;
        var modelId = await SeedModelAsync(host);

        using var response = await SendAsync(host, modelId);

        var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.InternalServerError);
        problem.GetProperty("type").GetString().Should().Be("/errors/test-completion-failed");
        var detail = problem.GetProperty("detail").GetString();
        detail.Should().NotBeNullOrWhiteSpace("detail explains the provider failure");
        detail.Should().NotContain(
            "sk-test-redacted",
            "SQ-M3-15: a key echoed by the provider is redacted before the detail leaves the process");
        detail.Should().Contain("sk-REDACTED", "the redaction marker proves scrubbing, not truncation");
        Encoding.UTF8.GetByteCount(detail!)
            .Should().BeLessOrEqualTo(256, "the detail is clamped to 256 UTF-8 bytes (M3T2's Utf8Excerpt)");
        (await AuditRowsAsync(factory, modelId)).Should().BeEmpty(
            "an incomplete call writes no test_model_completion row");
    }

    /// <summary>Task row: SQ-M3-14 — a provider with no registered plugin fails clearly.</summary>
    [Fact]
    public async Task UnknownProvider_Returns500_ClearError()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;
        var requestsBefore = TotalRequests();
        var modelId = await SeedModelAsync(host, provider: "acme-llm");

        using var response = await SendAsync(host, modelId);

        var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.InternalServerError);
        problem.GetProperty("type").GetString().Should().Be("/errors/test-completion-failed");
        problem.GetProperty("detail").GetString().Should().Contain(
            "acme-llm", "the failure names the provider so an admin can act on it");
        TotalRequests().Should().Be(requestsBefore, "no plugin → no engine call");
        (await AuditRowsAsync(factory, modelId)).Should().BeEmpty();
    }

    /// <summary>Task row: anonymous 401 and authenticated non-admin 403 (SQ-M3-1 route-wide gate).</summary>
    [Fact]
    public async Task NonAdmin_Returns403()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;
        var modelId = await SeedModelAsync(host);
        var path = $"/v1/admin/models/{modelId}/test-completion";

        // Anonymous → 401 RFC 7807 (JwtBearer challenge event).
        using var anonymous = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent(ValidBody()),
            });
        (await AccountTestHost.ReadProblemAsync(anonymous, HttpStatusCode.Unauthorized))
            .GetProperty("type").GetString().Should().Be("/errors/unauthorized");

        // Authenticated non-admin → 403 before the handler runs.
        var userId = await AdminTestHost.SeedUserAsync(
            factory, $"m3t6-nonadmin-{Guid.NewGuid():N}@example.test", UserRole.User);
        var userBearer = await AdminTestHost.BearerAsync(factory, userId, "User");
        using var forbidden = await client.SendAsync(
            AdminTestHost.Request(HttpMethod.Post, path, userBearer, ValidBody()));
        (await AccountTestHost.ReadProblemAsync(forbidden, HttpStatusCode.Forbidden))
            .GetProperty("type").GetString().Should().Be("/errors/forbidden");

        // …while the seeded Admin walks right through (role plumbing proof).
        using var allowed = await SendAsync(host, modelId);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, "the Admin role reaches the handler");
    }

    /// <summary>Task row: 61 requests in one minute → the 61st is 429 + Retry-After.</summary>
    [Fact]
    public async Task RateLimited_PerAdmin_Returns429()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;

        // Land inside the first ~55 s of an epoch window — AFTER the host is
        // up — so the whole burst fits one 60 s budget (M1T4 review finding 3a).
        await AccountTestHost.WaitForFreshRateLimitWindowAsync();

        // An UNKNOWN model id still burns budget: AuthenticatedRateLimitMiddleware
        // counts before it calls next, and every 404 is an authenticated request.
        var path = $"/v1/admin/models/{Guid.NewGuid()}/test-completion";
        HttpResponseMessage? last = null;
        for (var attempt = 0; attempt < 61; attempt++)
        {
            last = await client.SendAsync(
                AdminTestHost.Request(HttpMethod.Post, path, host.Bearer, ValidBody()));
            if (last.StatusCode == HttpStatusCode.TooManyRequests)
            {
                break;
            }
        }

        last.Should().NotBeNull();
        last!.StatusCode.Should().Be(
            HttpStatusCode.TooManyRequests, "the 61st request in the window exceeds 60/min");
        last.Headers.Contains("Retry-After").Should().BeTrue("§13.14: 429 carries Retry-After");
        var problem = await AccountTestHost.ReadProblemAsync(last, HttpStatusCode.TooManyRequests);
        problem.GetProperty("type").GetString().Should().Be("/errors/rate-limited");
        AssertRateLimitHeaders(last, "429 problem document");
    }

    // -----------------------------------------------------------------------
    // Cost derivation (M3T4 helper, SQ-M3-17)
    // -----------------------------------------------------------------------

    /// <summary>Task row: no usage.cost → price-derived cost over the provider's counts.</summary>
    [Fact]
    public async Task CostFromPrices_PopulatesCostUsd()
    {
        var host = await CreateHostAsync(scenario: "cost-prices-non-streaming");
        await using var factory = host.Factory;
        using var client = host.Client;
        var modelId = await SeedModelAsync(host, priceInput: 0.005m, priceOutput: 0.015m);

        using var response = await SendAsync(host, modelId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = document.RootElement.Clone();
        body.GetProperty("prompt_tokens").GetInt32().Should().Be(100);
        body.GetProperty("completion_tokens").GetInt32().Should().Be(50);
        body.GetProperty("total_tokens").GetInt32().Should().Be(150);
        body.GetProperty("cost_usd").GetDecimal().Should().Be(
            0.00125m, "(100/1000)*0.005 + (50/1000)*0.015 = 0.00125");
    }

    /// <summary>Task row: usage.cost beats the seeded prices (SQ-M3-17 rule 1b).</summary>
    [Fact]
    public async Task ProviderCost_TakesPrecedence()
    {
        var host = await CreateHostAsync(scenario: "provider-cost-non-streaming");
        await using var factory = host.Factory;
        using var client = host.Client;
        var modelId = await SeedModelAsync(host); // prices 0.02/0.01 would give 0.00012

        using var response = await SendAsync(host, modelId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = document.RootElement.Clone();
        body.GetProperty("prompt_tokens").GetInt32().Should().Be(5);
        body.GetProperty("cost_usd").GetDecimal().Should().Be(
            0.0007m, "the provider's usage.cost wins over the price fallback");
    }

    // -----------------------------------------------------------------------
    // §13.14 envelope + request-shape validation
    // -----------------------------------------------------------------------

    /// <summary>Task row: every authenticated response of this route carries the §13.14 headers.</summary>
    [Fact]
    public async Task AllAuthenticatedResponses_EmitXRateLimitHeaders()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;
        var modelId = await SeedModelAsync(host);

        using var ok = await SendAsync(host, modelId);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertRateLimitHeaders(ok, "POST 200");

        using var invalid = await client.SendAsync(
            AdminTestHost.Request(HttpMethod.Post, Url(modelId), host.Bearer, new { user_prompt = " " }));
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertRateLimitHeaders(invalid, "400 validation problem document");

        using var missing = await SendAsync(host, Guid.NewGuid());
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        AssertRateLimitHeaders(missing, "404 problem document");
    }

    /// <summary>
    /// Task error table: EVERY input-shape failure is the single 400
    /// /errors/config-validation class (absent body, empty prompt, bad
    /// tools[], duplicate tool names) — and none of them reaches the engine
    /// or the audit table.
    /// </summary>
    [Fact]
    public async Task InvalidRequestShape_Returns400_ConfigValidation()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;
        var requestsBefore = TotalRequests();
        var modelId = await SeedModelAsync(host);

        // (a) absent body → errors[] blames `body`.
        using (var response = await SendAsync(host, modelId, body: null, emptyBody: true))
        {
            var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.BadRequest);
            problem.GetProperty("type").GetString().Should().Be("/errors/config-validation");
            ErrorNames(problem).Should().Contain("body", "an absent body is an input error, not a 500");
        }

        // (b) malformed JSON.
        using (var response = await SendRawAsync(host, modelId, "{not json"))
        {
            var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.BadRequest);
            problem.GetProperty("type").GetString().Should().Be("/errors/config-validation");
            ErrorNames(problem).Should().Contain("body");
        }

        // (c) blank user_prompt → errors[] blames `user_prompt`.
        using (var response = await SendAsync(host, modelId, new { user_prompt = "   " }))
        {
            var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.BadRequest);
            problem.GetProperty("type").GetString().Should().Be("/errors/config-validation");
            ErrorNames(problem).Should().Contain("user_prompt");
        }

        // (d) tools[].parameters that is not a JSON object.
        using (var response = await SendAsync(host, modelId, new
        {
            user_prompt = UserPrompt,
            tools = new object[]
            {
                new { name = "ping", description = "d", parameters = new[] { "not", "an", "object" } },
            },
        }))
        {
            var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.BadRequest);
            problem.GetProperty("type").GetString().Should().Be("/errors/config-validation");
            ErrorNames(problem).Should().Contain(
                "tools[0].parameters", "the child rule keeps its collection index in the wire name");
        }

        // (e) duplicate tool names → rejected before ToDictionary could throw.
        using (var response = await SendAsync(host, modelId, new
        {
            user_prompt = UserPrompt,
            tools = new object[]
            {
                new { name = "ping", description = "one", parameters = new { type = "object" } },
                new { name = "ping", description = "two", parameters = new { type = "object" } },
            },
        }))
        {
            var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.BadRequest);
            problem.GetProperty("type").GetString().Should().Be("/errors/config-validation");
            ErrorNames(problem).Should().Contain("tools");
        }

        // (f) a NULL element in tools[] → the same 400 class (review fix:
        // this used to NRE inside the child rules and escape as a 500).
        using (var response = await SendAsync(host, modelId, new
        {
            user_prompt = UserPrompt,
            tools = new object?[] { null },
        }))
        {
            var problem = await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.BadRequest);
            problem.GetProperty("type").GetString().Should().Be("/errors/config-validation");
            ErrorNames(problem).Should().Contain(
                "tools[0]", "a null tools[] entry is an indexed input error, never a server error");
        }

        TotalRequests().Should().Be(
            requestsBefore, "a rejected request runs NO engine call");
        (await AuditRowsAsync(factory, modelId)).Should().BeEmpty(
            "test_model_completion records COMPLETED calls only (SQ-M3-4)");
    }

    /// <summary>Task row: the declared tools reach the provider as OpenAI function declarations.</summary>
    [Fact]
    public async Task Tools_RoundTrip_InWireBody()
    {
        var host = await CreateHostAsync();
        await using var factory = host.Factory;
        using var client = host.Client;
        var modelId = await SeedModelAsync(host);

        using var response = await SendAsync(host, modelId, new
        {
            system_prompt = SystemPrompt,
            user_prompt = UserPrompt,
            tools = new object[]
            {
                new
                {
                    name = "get_weather",
                    description = "Looks up the weather for a city.",
                    parameters = new
                    {
                        type = "object",
                        properties = new { city = new { type = "string" } },
                    },
                },
            },
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var sent = _fixture.LastRequestBody;
        sent.Should().NotBeNull("the engine posted to the in-process mock");
        using var document = JsonDocument.Parse(sent!);
        var body = document.RootElement.Clone();

        var messages = body.GetProperty("messages").EnumerateArray().ToList();
        messages.Should().HaveCount(2, "SQ-M3-9 splits the prompt on the SYSTEM/USER markers");
        messages[0].GetProperty("role").GetString().Should().Be("system");
        messages[0].GetProperty("content").GetString().Should().Be(SystemPrompt);
        messages[1].GetProperty("role").GetString().Should().Be("user");
        messages[1].GetProperty("content").GetString().Should().Be(UserPrompt);

        var tool = body.GetProperty("tools").EnumerateArray().Single();
        tool.GetProperty("type").GetString().Should().Be("function");
        var function = tool.GetProperty("function");
        function.GetProperty("name").GetString().Should().Be("get_weather");
        function.GetProperty("description").GetString().Should().Be(
            "Looks up the weather for a city.", "the DTO description rides the schema root");
        function.GetProperty("parameters")
            .GetProperty("city").GetProperty("type").GetString().Should().Be(
                "string", "the schema's properties object is transmitted verbatim");
        body.GetProperty("tool_choice").GetString().Should().Be("auto");
    }

    // -----------------------------------------------------------------------
    // Lifecycle (one class instance per test) + per-test cleanup
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

        await AccountTestHost.MigrateAsync(_postgres);
        await using var db = new EmailAgentDbContext(
            new DbContextOptionsBuilder<EmailAgentDbContext>()
                .UseNpgsql(_postgres.ConnectionString)
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
        HttpClient Client,
        Guid AdminId,
        string Bearer,
        string Marker);

    /// <summary>
    /// One host per test: migrated shared Postgres, a seeded Admin with a
    /// real issuer-minted token, this class's OpenAIMock pinned to the asked
    /// scenario and a fresh unique marker (cleanup key).
    /// </summary>
    /// <remarks>
    /// The engine is registered into <b>IPluginRegistry</b> — the seam M3T6
    /// actually resolves (<c>provider</c> → engine, SQ-M3-14) — and only AFTER
    /// the host is booted: <c>CreateClient</c> starts the hosted services, and
    /// PluginLoader.StartAsync awaits a ReloadAsync that CLEARS the registry
    /// (its per-factory plugins directory does not exist, so it registers
    /// nothing itself). Registering afterwards is what keeps the mock-backed
    /// engine in place for the whole test.
    /// </remarks>
    private async Task<TestHost> CreateHostAsync(string scenario = "happy")
    {
        var marker = $"m3t6-{Guid.NewGuid():N}";
        _marker = marker;

        // Deterministic mock state before any completion can run.
        await _fixture.SetScenarioAsync(scenario);

        var connectionString = await AccountTestHost.MigrateAsync(_postgres);
        var factory = ApiTestFactory.Create(connectionString);
        var client = AccountTestHost.CreateClient(factory); // boots the host (hosted services included)

        factory.Services.GetRequiredService<IPluginRegistry>().RegisterAgentEngine(
            Provider, new OpenAICompatibleAgentEngine(_fixture.Client));

        var adminId = await AdminTestHost.SeedUserAsync(
            factory, $"{marker}@example.test", UserRole.Admin);
        var bearer = await AdminTestHost.BearerAsync(factory, adminId, "Admin");
        return new TestHost(factory, client, adminId, bearer, marker);
    }

    // -----------------------------------------------------------------------
    // Request/response helpers
    // -----------------------------------------------------------------------

    private static string Url(Guid modelId) => $"/v1/admin/models/{modelId}/test-completion";

    private static object ValidBody() => new { system_prompt = SystemPrompt, user_prompt = UserPrompt };

    private static StringContent JsonContent(object payload) => new(
        JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private Task<HttpResponseMessage> SendAsync(
        TestHost host, Guid modelId, object? body = null, bool emptyBody = false)
        => SendRawAsync(
            host,
            modelId,
            emptyBody ? null : JsonSerializer.Serialize(body ?? ValidBody()));

    private Task<HttpResponseMessage> SendRawAsync(TestHost host, Guid modelId, string? json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url(modelId))
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", host.Bearer) },
        };
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return host.Client.SendAsync(request);
    }

    /// <summary>The RFC 7807 errors[] wire names of a problem document.</summary>
    private static string[] ErrorNames(JsonElement problem)
        => problem.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
            ? errors.EnumerateArray()
                .Select(error => error.GetProperty("name").GetString() ?? string.Empty)
                .ToArray()
            : [];

    /// <summary>§13.14: every authenticated response carries X-RateLimit-Limit/-Remaining/-Reset.</summary>
    private static void AssertRateLimitHeaders(HttpResponseMessage response, string what)
    {
        response.Headers.Contains("X-RateLimit-Limit").Should().BeTrue($"{what}: X-RateLimit-Limit (§13.14)");
        int.TryParse(response.Headers.GetValues("X-RateLimit-Limit").Single(), out var limit)
            .Should().BeTrue("X-RateLimit-Limit is an integer");
        limit.Should().Be(60, "the M1T4 authenticated per-user budget");
        response.Headers.Contains("X-RateLimit-Remaining").Should().BeTrue($"{what}: X-RateLimit-Remaining");
        int.TryParse(response.Headers.GetValues("X-RateLimit-Remaining").Single(), out var remaining)
            .Should().BeTrue("X-RateLimit-Remaining is an integer");
        remaining.Should().BeGreaterThanOrEqualTo(0);
        response.Headers.Contains("X-RateLimit-Reset").Should().BeTrue($"{what}: X-RateLimit-Reset");
        long.TryParse(response.Headers.GetValues("X-RateLimit-Reset").Single(), out var reset)
            .Should().BeTrue("X-RateLimit-Reset is unix seconds");
        reset.Should().BeGreaterThan(0);
    }

    /// <summary>Independent re-derivation of the documented prompt hash (never the implementation's own).</summary>
    private static string ExpectedPromptHash(string systemPrompt, string userPrompt)
        => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes($"{systemPrompt}\n{userPrompt}")))
            .ToLowerInvariant();

    /// <summary>Engine round-trips against this fixture's mock, summed over scenarios.</summary>
    private int TotalRequests() => _fixture.RequestCounts.Values.Sum();

    // -----------------------------------------------------------------------
    // Database helpers
    // -----------------------------------------------------------------------

    /// <summary>Seeds the persisted state the endpoint consumes: catalog row + encrypted key blob.</summary>
    private async Task<Guid> SeedModelAsync(
        TestHost host,
        bool enabled = true,
        string provider = Provider,
        decimal? priceInput = 0.02m,
        decimal? priceOutput = 0.01m)
    {
        using var scope = host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<EncryptedModelApiKeyStore>();
        var blobId = await store.WriteAsync(ApiKey, CancellationToken.None);

        var id = Guid.NewGuid();
        db.ModelCatalog.Add(
            new ModelCatalogEntry
            {
                Id = id,
                DisplayName = $"{host.Marker} model",
                Provider = provider,
                BaseUrl = _fixture.BaseUrl,
                ModelName = host.Marker,
                Enabled = enabled,
                CreatedBy = host.AdminId,
                CreatedAt = DateTimeOffset.UtcNow,
                ApiKeySecretRef = blobId.ToString(),
                PricePer1kInputUsd = priceInput,
                PricePer1kOutputUsd = priceOutput,
            });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<List<AuditLog>> AuditRowsAsync(
        WebApplicationFactory<Program> factory, Guid modelId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return await db.AuditLog.AsNoTracking()
            .Where(row => row.Action == AuditActions.TestModelCompletion
                          && row.TargetId == modelId.ToString())
            .OrderBy(row => row.Id)
            .ToListAsync();
    }

    /// <summary>The single audit row of <paramref name="action"/> targeting this model id.</summary>
    private static async Task<AuditLog> SingleAuditAsync(
        WebApplicationFactory<Program> factory, string action, Guid modelId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var rows = await db.AuditLog.AsNoTracking()
            .Where(row => row.Action == action && row.TargetId == modelId.ToString())
            .OrderBy(row => row.Id)
            .ToListAsync();
        rows.Should().ContainSingle(
            $"exactly one {action} row must target {modelId} — found {rows.Count}");
        return rows[0];
    }
}
