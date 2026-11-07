using System.Net;
using Bunit;
using EmailAgent.Spa.Notifications;
using FluentAssertions;
using Xunit;

// The models page lives in EmailAgent.Spa.Pages.Admin; it is aliased because
// the unit-test project's own namespace tree has EmailAgent.UnitTests.Models.
using ModelsPage = EmailAgent.Spa.Pages.Admin.Models;

namespace EmailAgent.UnitTests.Spa;

/// <summary>
/// M3T5 bUnit coverage for the /admin/models page (task test list): the empty
/// state, the seven-column table with its four row actions, the add-dialog
/// handoff, the delete-confirm flow, the client-side Admin gate, the 403
/// redirect, the friendly 429 banner and the Enable/Disable PATCH — all over
/// <see cref="StubHttpHandler"/>, never a real service (AGENTS.md hard
/// constraint 3).
/// </summary>
public class AdminModelsPageTests
{
    private static readonly Guid ModelId = new("44444444-4444-4444-4444-444444444444");

    private const string EmptyListJson = """{"items":[],"next_cursor":null}""";

    private const string ModelName = "gpt-4o-mini";

    private const string RowJson =
        """
        {"items":[{"id":"44444444-4444-4444-4444-444444444444","display_name":"mini chat","provider":"openai-compatible","base_url":"https://api.example.test/v1","model_name":"gpt-4o-mini","enabled":true,"tool_calling_probed_at":"2026-01-02T10:30:00+00:00","tool_calling_probe_result":"Pass","created_by":"11111111-1111-1111-1111-111111111111","created_at":"2026-01-01T00:00:00+00:00","price_per_1k_input_usd":0.02,"price_per_1k_output_usd":0.01}],"next_cursor":null}
        """;

    /// <summary>The same row after the Enable/Disable PATCH flipped the flag (probe re-ran).</summary>
    private const string DisabledRowJson =
        """
        {"items":[{"id":"44444444-4444-4444-4444-444444444444","display_name":"mini chat","provider":"openai-compatible","base_url":"https://api.example.test/v1","model_name":"gpt-4o-mini","enabled":false,"tool_calling_probed_at":"2026-01-02T10:30:00+00:00","tool_calling_probe_result":"Pass","created_by":"11111111-1111-1111-1111-111111111111","created_at":"2026-01-01T00:00:00+00:00","price_per_1k_input_usd":0.02,"price_per_1k_output_usd":0.01}],"next_cursor":null}
        """;

    // -----------------------------------------------------------------
    // States
    // -----------------------------------------------------------------

    [Fact]
    public void ModelsPage_EmptyState_ShowsEmptyMessage_AndAddButton()
    {
        using var ctx = AdminContext();

        var cut = ctx.RenderComponent<ModelsPage>();

        cut.WaitForAssertion(() => cut.FindAll("#models-empty")
            .Should().ContainSingle("an admin session with no models shows the empty state"));
        cut.Markup.Should().Contain("No models configured. Click 'Add model' to add one.");
        cut.Find("#add-model-open").TextContent.Should().Be("Add model");
        cut.Find("#models-empty-add").TextContent.Should().Be("Add model",
            "the empty state carries its own shortcut as the task asks");
        cut.Find("#models-reload").TextContent.Should().Be("Reload");
        cut.FindAll("#models-table").Should().BeEmpty();

        ctx.Handler.Requests.Should().ContainSingle(
            r => r.Path == "/v1/admin/models?limit=25",
            "the list loads once, from GET /v1/admin/models");
    }

    [Fact]
    public void ModelsPage_WithRows_RendersTable_WithActionButtons()
    {
        using var ctx = AdminContext(RowJson);

        var cut = ctx.RenderComponent<ModelsPage>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("mini chat"));

        var headers = cut.FindAll("th").Select(th => th.TextContent).ToList();
        headers.Should().HaveCount(7, "the task's seven-column table");
        headers.Should().Equal(
            "Display name", "Provider", "Model name", "Enabled",
            "Probed at", "Probe result", "Actions");

        var cells = cut.FindAll("tbody tr td").Select(td => td.TextContent).ToList();
        cells.Should().HaveCount(7, "one row × seven columns");
        cells[0].Should().Be("mini chat");
        cells[1].Should().Be("openai-compatible");
        cells[2].Should().Be(ModelName);
        cells[3].Should().Be("Enabled");
        cells[4].Should().NotBe("—", "tool_calling_probed_at renders in the viewer's local time");
        cells[5].Should().Be("Pass");

        cut.FindAll(".badge-ok").Should().HaveCount(2,
            "enabled + probe Pass are both green");
        cut.FindAll(".badge-fail").Should().BeEmpty();
        cut.Find($"#model-edit-{ModelId:N}").TextContent.Should().Be("Edit");
        cut.Find($"#model-test-{ModelId:N}").TextContent.Should().Be("Test completion");
        cut.Find($"#model-toggle-{ModelId:N}").TextContent.Should().Be("Disable",
            "an enabled row offers its inverse");
        cut.Find($"#model-delete-{ModelId:N}").TextContent.Should().Be("Delete");
    }

    [Fact]
    public void ModelsPage_AddButton_OpensEditDialog_WithEmptyForm()
    {
        using var ctx = AdminContext();

        var cut = ctx.RenderComponent<ModelsPage>();
        cut.WaitForAssertion(() => cut.Find("#add-model-open").Should().NotBeNull());

        cut.Find("#add-model-open").Click();

        cut.Find("#model-display-name").GetAttribute("value").Should().BeNullOrEmpty(
            "add mode starts with a blank display name");
        cut.Find("#model-base-url").GetAttribute("value").Should().BeNullOrEmpty();
        cut.Find("#model-model-name").GetAttribute("value").Should().BeNullOrEmpty();
        cut.Find("#model-api-key").GetAttribute("value").Should().BeNullOrEmpty(
            "the credential field is blank on open, add or edit");
        cut.Find("#model-api-key").GetAttribute("type").Should().Be("password",
            "api_key lives only in a password input (task scope)");
        cut.Find("#model-enabled").HasAttribute("checked").Should().BeTrue(
            "the enabled toggle defaults to true per the task's field list");
        cut.Find("#model-provider").HasAttribute("readonly").Should().BeTrue(
            "provider is read-only: openai-compatible is the only M3 plugin (SQ-M3-14)");
        cut.Find("#model-provider").GetAttribute("value").Should().Be("openai-compatible");
        cut.Find("#model-api-key-note").TextContent.Should().Contain(
            "Required", "create mode requires a key (task scope bullet)");

        ctx.Handler.Requests.Should().ContainSingle(
            r => r.Path == "/v1/admin/models?limit=25",
            "add mode never fetches an existing model (no GET /v1/admin/models/{id})");
    }

    // -----------------------------------------------------------------
    // Role gate
    // -----------------------------------------------------------------

    [Fact]
    public void ModelsPage_NonAdminRole_RedirectsToLogin()
    {
        using var ctx = new SpaTestContext();
        ctx.Store.Set(SpaTestContext.FakeJwt(role: "User"));

        var cut = ctx.RenderComponent<ModelsPage>();

        cut.WaitForAssertion(() => ctx.Nav.Uri.Should().Be(
            "http://localhost/account/login",
            "a non-admin token must never reach the admin catalog"));
        ctx.Handler.Requests.Should().BeEmpty(
            "the client-side role gate fires before any API call");
        cut.FindAll("#models-empty").Should().BeEmpty();
        cut.FindAll("#models-table").Should().BeEmpty();
    }

    /// <summary>
    /// M1T8 Users.razor parity: from an Admin-only namespace a 403 is a
    /// session verdict (disabled account, stale role), so the SPA leaves the
    /// page instead of rendering a table it can never fill.
    /// </summary>
    [Fact]
    public void ModelsPage_Forbidden403_RedirectsToLogin()
    {
        using var ctx = AdminContext();
        ctx.Handler.Responder = (_, _) => Task.FromResult(SpaTestContext.Json(
            """
            {"type":"/errors/forbidden","title":"Forbidden","status":403,"detail":"This account is disabled."}
            """,
            HttpStatusCode.Forbidden));

        var cut = ctx.RenderComponent<ModelsPage>();

        cut.WaitForAssertion(() => ctx.Nav.Uri.Should().Be(
            "http://localhost/account/login",
            "403 from the admin catalog bounces the session to login"));
        cut.FindAll("#models-empty").Should().BeEmpty(
            "no empty state next to a refusal to load");
        cut.FindAll("#models-table").Should().BeEmpty();
    }

    /// <summary>The rate limit stays on the page, in plain words (task invariant: 429 graceful).</summary>
    [Fact]
    public void ModelsPage_RateLimited429_ShowsFriendlyInlineError()
    {
        using var ctx = AdminContext();
        ctx.Handler.Responder = (_, _) => Task.FromResult(SpaTestContext.Json(
            """
            {"type":"/errors/rate-limited","title":"Too Many Requests","status":429,"detail":"The limit is 60 requests per minute."}
            """,
            HttpStatusCode.TooManyRequests));

        var cut = ctx.RenderComponent<ModelsPage>();

        cut.WaitForAssertion(() =>
        {
            var error = cut.Find("#models-error");
            error.GetAttribute("role").Should().Be("alert");
            error.TextContent.Should().Contain(
                "Too many requests — wait a moment and try again.",
                "a 429 tells the admin what to do instead of a bare status");
            error.TextContent.Should().Contain("60 requests per minute");
        });
        ctx.Nav.Uri.Should().Be("http://localhost/", "429 never redirects");
        cut.FindAll("#models-empty").Should().BeEmpty(
            "the first page never arrived, so the empty state stays out");
    }

    // -----------------------------------------------------------------
    // Enable / Disable (PATCH the inverse flag)
    // -----------------------------------------------------------------

    [Fact]
    public void ModelsPage_Toggle_PatchesInverseEnabled_AndRefreshesRow()
    {
        using var ctx = AdminContext(RowJson);
        var disabled = false;
        ctx.Handler.Responder = (request, _) =>
        {
            if (request.Method == HttpMethod.Patch)
            {
                disabled = true;
                return Task.FromResult(SpaTestContext.Json(DisabledRowJson));
            }

            return Task.FromResult(SpaTestContext.Json(
                disabled ? DisabledRowJson : RowJson));
        };

        var cut = ctx.RenderComponent<ModelsPage>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("mini chat"));

        cut.Find($"#model-toggle-{ModelId:N}").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find($"#model-toggle-{ModelId:N}").TextContent.Should().Be("Enable",
                "the reloaded row now offers the inverse of the stored flag");
            cut.Markup.Should().Contain("Disabled");
        });

        var patch = ctx.Handler.Requests.Should().ContainSingle(
                r => r.Method == "PATCH"
                    && r.Path == $"/v1/admin/models/{ModelId}")
            .Which;
        patch.Body.Should().Contain("\"enabled\":false",
            "the row PATCHes exactly the inverse of its current flag (task scope)");
        ctx.Handler.Requests.Count(r => r.Path == "/v1/admin/models?limit=25")
            .Should().Be(2, "load on init plus the reload after the toggle");
        ctx.Toasts.Items.Should().BeEmpty(
            "the task reserves toasts for the added/deleted messages");
        cut.FindAll("#models-error").Should().BeEmpty();
    }

    // -----------------------------------------------------------------
    // Delete
    // -----------------------------------------------------------------

    [Fact]
    public void ModelsPage_Delete_OpensConfirmModal_AndRemovesRow_OnConfirm()
    {
        using var ctx = new SpaTestContext();
        ctx.Store.Set(SpaTestContext.FakeJwt(role: "Admin"));
        var deleted = false;
        ctx.Handler.Responder = (request, _) =>
        {
            if (request.Method == HttpMethod.Delete)
            {
                deleted = true;
                return Task.FromResult(SpaTestContext.NoContent());
            }

            return Task.FromResult(
                SpaTestContext.Json(deleted ? EmptyListJson : RowJson));
        };

        var cut = ctx.RenderComponent<ModelsPage>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("mini chat"));
        var toasts = ctx.RenderComponent<ToastContainer>();

        cut.Find($"#model-delete-{ModelId:N}").Click();
        cut.Find("#delete-model-title").TextContent.Should().Be("Delete model");
        cut.Markup.Should().Contain("This cannot be undone.");

        cut.Find("#delete-model-confirm").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("#delete-model-title").Should().BeEmpty(
                "the modal closes after the API accepts the delete");
            cut.Find("#models-empty").Should().NotBeNull(
                "the post-delete reload now returns an empty page");
        });
        ctx.Handler.Requests.Should().ContainSingle(
            r => r.Method == "DELETE"
                && r.Path == $"/v1/admin/models/{ModelId}");
        toasts.WaitForAssertion(() => ctx.Toasts.Items.Should().ContainSingle(
            t => t.Kind == ToastKind.Success
                && t.Message == "Model 'mini chat' deleted."));
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    /// <summary>An Admin-token context whose list endpoint answers <paramref name="listJson"/>.</summary>
    private static SpaTestContext AdminContext(string listJson = EmptyListJson)
    {
        var ctx = new SpaTestContext();
        ctx.Store.Set(SpaTestContext.FakeJwt(role: "Admin"));
        ctx.Handler.Responder = (request, _) => Task.FromResult(SpaTestContext.Json(
            request.RequestUri!.AbsolutePath.StartsWith("/v1/admin/models", StringComparison.Ordinal)
                ? listJson
                : "{}"));
        return ctx;
    }
}
