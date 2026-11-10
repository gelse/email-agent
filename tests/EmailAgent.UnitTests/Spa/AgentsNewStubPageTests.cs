using System.Net;
using Bunit;
using FluentAssertions;
using Xunit;

// The stub page lives in EmailAgent.Spa.Pages.User; it is aliased so the bare
// name can never collide with anything else in this namespace tree.
using AgentsNewStub = EmailAgent.Spa.Pages.User.AgentsNewStub;

namespace EmailAgent.UnitTests.Spa;

/// <summary>
/// M3T7 bUnit coverage for the /user/agents/new STUB agent-config page: the
/// picker built from the server's reduced allow-list, the URL-hash
/// acknowledgement of a selection, the deliberately absent save action, the
/// client-side (User, Admin) role gate and the zero-models state — all over
/// <see cref="StubHttpHandler"/>, never a real service (AGENTS.md hard
/// constraint 3). This is the unit half of M3 exit criterion 1's "a user can
/// select it in a stub agent config"; the end-to-end half is
/// <c>StubAgentPageTests.User_StubAgentPage_ModelPicker_FiltersToPassEnabled</c>.
/// </summary>
public class AgentsNewStubPageTests
{
    private static readonly Guid PassingId = new("44444444-4444-4444-4444-444444444444");

    private static readonly Guid SecondId = new("55555555-5555-5555-5555-555555555555");

    private const string EmptyListJson = """{"items":[]}""";

    /// <summary>Two already-allow-listed rows — the SPA renders, never filters.</summary>
    private const string TwoRowsJson =
        """
        {"items":[{"id":"44444444-4444-4444-4444-444444444444","display_name":"mini chat","provider":"openai-compatible","model_name":"gpt-4o-mini"},{"id":"55555555-5555-5555-5555-555555555555","display_name":"big chat","provider":"openai-compatible","model_name":"gpt-4o"}]}
        """;

    // -----------------------------------------------------------------
    // Picker
    // -----------------------------------------------------------------

    [Fact]
    public void AgentNewStub_Picker_RendersOneOptionPerAllowListedModel_WithIdValues()
    {
        using var ctx = UserContext(TwoRowsJson);

        var cut = ctx.RenderComponent<AgentsNewStub>();

        cut.WaitForAssertion(() => cut.Find("#agent-model-picker").Should().NotBeNull(
            "the picker renders as soon as GET /v1/user/models answers"));
        cut.Find("label[for='agent-model-picker']").TextContent.Should().Be("Model");

        var options = cut.FindAll("#agent-model-picker option").ToList();
        options.Should().HaveCount(3, "one placeholder plus one option per returned row");
        options[0].GetAttribute("value").Should().BeNullOrEmpty(
            "the placeholder carries no model id");

        var models = options.Skip(1).ToList();

        // Deliberately NO `because` argument on these two Equal() calls: on a
        // string collection FluentAssertions binds Equal(params string[]), so a
        // trailing reason string would be consumed as a THIRD expected value and
        // the test would fail on its own assertion text (M3T7 gate 3, W-1).
        models.Select(option => option.GetAttribute("value")).Should().Equal(
            PassingId.ToString(), SecondId.ToString());
        models.Select(option => option.TextContent).Should().Equal(
            "mini chat", "big chat");

        ctx.Handler.Requests.Should().ContainSingle(
            request => request.Path == "/v1/user/models?limit=100",
            "the page asks the server for the full pickable page exactly once");
        ctx.Handler.Requests.Should().OnlyContain(
            request => request.Path.StartsWith("/v1/user/models", StringComparison.Ordinal),
            "the stub page touches no other API surface");
    }

    // -----------------------------------------------------------------
    // Selection → URL hash (task: acknowledged "for the future M5/M6a CRUD")
    // -----------------------------------------------------------------

    [Fact]
    public void AgentNewStub_Selection_AcknowledgesModelIdInUrlHash()
    {
        using var ctx = UserContext(TwoRowsJson);
        ctx.Nav.NavigateTo("/user/agents/new");
        var cut = ctx.RenderComponent<AgentsNewStub>();
        cut.WaitForAssertion(() => cut.Find("#agent-model-picker").Should().NotBeNull());

        cut.Find("#agent-model-picker").Change(SecondId.ToString());

        ctx.Nav.Uri.Should().Be(
            $"http://localhost/user/agents/new#model={SecondId}",
            "forceLoad: false keeps this a client-side acknowledgement — no reload");

        // A later re-pick overwrites the hash in place rather than stacking.
        cut.Find("#agent-model-picker").Change(PassingId.ToString());
        ctx.Nav.Uri.Should().Be(
            $"http://localhost/user/agents/new#model={PassingId}",
            "the hash always holds the CURRENT selection");

        // The placeholder is "nothing selected": the page parses the value,
        // finds no GUID and deliberately navigates NOWHERE rather than to a
        // bogus #model= hash.
        cut.Find("#agent-model-picker").Change(string.Empty);
        ctx.Nav.Uri.Should().Be(
            $"http://localhost/user/agents/new#model={PassingId}",
            "an empty selection must not navigate to #model=");
    }

    // -----------------------------------------------------------------
    // No save action (task scope Out bullet: CRUD lands in M5/M6a)
    // -----------------------------------------------------------------

    [Fact]
    public void AgentNewStub_HasNoSaveAction_RendersTheTaskMandatedNote()
    {
        using var ctx = UserContext(TwoRowsJson);

        var cut = ctx.RenderComponent<AgentsNewStub>();

        cut.WaitForAssertion(() => cut.Find("#agent-new-no-save").Should().NotBeNull());
        cut.FindAll("button").Should().BeEmpty(
            "the stub ships no save button — a control with nothing to call would be a lie");
        cut.FindAll("input[type='submit']").Should().BeEmpty();
        cut.Find("#agent-new-no-save").TextContent.Should().Be(
            "Save action lands in M5. The selected model id is in the URL hash for future use.");
        cut.FindAll("#agent-new-error").Should().BeEmpty();
    }

    // -----------------------------------------------------------------
    // Client-side role gate (the page's own [Authorize(Roles = "User, Admin")])
    // -----------------------------------------------------------------

    [Fact]
    public void AgentNewStub_WithoutUserOrAdminRole_RedirectsToLogin()
    {
        using var ctx = new SpaTestContext();
        ctx.Store.Set(SpaTestContext.FakeJwt(role: "Support"));

        var cut = ctx.RenderComponent<AgentsNewStub>();

        cut.WaitForAssertion(() => ctx.Nav.Uri.Should().Be(
            "http://localhost/account/login",
            "only the User and Admin roles may reach the stub picker"));
        ctx.Handler.Requests.Should().BeEmpty(
            "the role gate fires before any API call");
        cut.FindAll("#agent-model-picker").Should().BeEmpty();
        cut.FindAll("#agent-new-no-save").Should().BeEmpty();
        cut.FindAll("#agent-new-error").Should().BeEmpty();
    }

    /// <summary>The mirror fact: the User role — the page's whole point — walks straight in.</summary>
    [Fact]
    public void AgentNewStub_WithUserRole_Renders()
    {
        using var ctx = UserContext(TwoRowsJson);

        var cut = ctx.RenderComponent<AgentsNewStub>();

        cut.WaitForAssertion(() => cut.Find("#agent-model-picker").Should().NotBeNull());
        ctx.Nav.Uri.Should().Be("http://localhost/", "a User is allowed — no redirect");
        cut.FindAll("#agent-new-empty").Should().BeEmpty();
    }

    // -----------------------------------------------------------------
    // States
    // -----------------------------------------------------------------

    [Fact]
    public void AgentNewStub_EmptyAllowList_ShowsEmptyState_InsteadOfAnEmptyPicker()
    {
        using var ctx = UserContext(EmptyListJson);

        var cut = ctx.RenderComponent<AgentsNewStub>();

        cut.WaitForAssertion(() => cut.Find("#agent-new-empty").Should().NotBeNull(
            "an allow-list of zero is a real state, not a failure"));
        cut.Find("#agent-new-empty").TextContent.Should().Be(
            "No models are available yet. Ask an administrator to add one.");
        cut.FindAll("#agent-model-picker").Should().BeEmpty(
            "no empty dropdown that would look selectable");
        cut.Find("#agent-new-no-save").TextContent.Should().Contain("Save action lands in M5.");
        cut.FindAll("#agent-new-error").Should().BeEmpty();
    }

    [Fact]
    public void AgentNewStub_ApiFailure_ShowsInlineErrorBox()
    {
        using var ctx = new SpaTestContext();
        ctx.Store.Set(SpaTestContext.FakeJwt(role: "User"));
        ctx.Handler.Responder = (_, _) => Task.FromResult(SpaTestContext.Problem(
            HttpStatusCode.ServiceUnavailable, "The service is temporarily unavailable."));

        var cut = ctx.RenderComponent<AgentsNewStub>();

        cut.WaitForAssertion(() => cut.Find("#agent-new-error").Should().NotBeNull(
            "a failed load stays on the page with an explanation"));
        cut.Find("#agent-new-error").GetAttribute("role").Should().Be("alert");
        cut.FindAll("#agent-model-picker").Should().BeEmpty();
        cut.FindAll("#agent-new-empty").Should().BeEmpty(
            "a failed fetch must not also claim 'no models exist' next to the error box");
        ctx.Nav.Uri.Should().Be("http://localhost/", "a load failure is not a session verdict");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    /// <summary>An ordinary User-role context whose picker endpoint answers <paramref name="listJson"/>.</summary>
    private static SpaTestContext UserContext(string listJson)
    {
        var ctx = new SpaTestContext();
        ctx.Store.Set(SpaTestContext.FakeJwt(role: "User"));
        ctx.Handler.Responder = (request, _) => Task.FromResult(SpaTestContext.Json(
            request.RequestUri!.AbsolutePath == "/v1/user/models" ? listJson : "{}"));
        return ctx;
    }
}
