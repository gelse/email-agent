using System.Net;
using System.Text;
using Bunit;
using EmailAgent.Spa.Models;
using EmailAgent.Spa.Pages.Admin;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Xunit;

// The models page lives in EmailAgent.Spa.Pages.Admin; it is aliased because
// the unit-test project's own namespace tree has EmailAgent.UnitTests.Models.
using ModelsPage = EmailAgent.Spa.Pages.Admin.Models;

namespace EmailAgent.UnitTests.Spa;

/// <summary>
/// M3T5 bUnit coverage for the hand-written add/edit model dialog (task test
/// list, Q-F4): the introspector-backed client-side required check (no
/// network on a form the schema already rejects), the base_url format rule,
/// the probe-failed red banner with the dialog left open, RFC 7807
/// <c>errors[]</c> rendered next to the named field, the create → close →
/// reload handoff through the parent page, and edit mode's blank-<c>api_key</c>
/// PATCH (task Risks "PATCH semantics on api_key").
/// </summary>
public class EditModelDialogTests
{
    private static readonly Guid ModelId = new("44444444-4444-4444-4444-444444444444");

    private const string EmptyListJson = """{"items":[],"next_cursor":null}""";

    private const string ModelJson =
        """
        {"id":"44444444-4444-4444-4444-444444444444","display_name":"mini chat","provider":"openai-compatible","base_url":"https://api.example.test/v1","model_name":"gpt-4o-mini","enabled":true,"tool_calling_probed_at":"2026-01-02T10:30:00+00:00","tool_calling_probe_result":"Pass","created_by":"11111111-1111-1111-1111-111111111111","created_at":"2026-01-01T00:00:00+00:00","price_per_1k_input_usd":0.02,"price_per_1k_output_usd":0.01}
        """;

    private const string CreatedJson =
        """
        {"id":"66666666-6666-6666-6666-666666666666","display_name":"mini chat","provider":"openai-compatible","base_url":"https://api.example.test/v1","model_name":"gpt-4o-mini","enabled":true,"tool_calling_probed_at":"2026-01-02T10:30:00+00:00","tool_calling_probe_result":"Pass","created_by":"11111111-1111-1111-1111-111111111111","created_at":"2026-01-01T00:00:00+00:00","price_per_1k_input_usd":0.02,"price_per_1k_output_usd":0.01}
        """;

    private const string CandidateKey = "sk-test-candidate";

    // -----------------------------------------------------------------
    // Client-side validation
    // -----------------------------------------------------------------

    [Fact]
    public void EditModelDialog_RequiredFields_ClientSideValidation()
    {
        using var ctx = new SpaTestContext();
        var cut = ctx.RenderComponent<EditModelDialog>();

        cut.Find("#model-save").Click();

        cut.Find("#error-display_name").TextContent.Should().Be("Display name is required.");
        cut.Find("#error-base_url").TextContent.Should().Be("Base URL is required.");
        cut.Find("#error-model_name").TextContent.Should().Be("Model name is required.");
        cut.Find("#error-api_key").TextContent.Should().Be("API key is required.",
            "create mode needs the key the schema requires (OpenAICompatibleConfigSchema)");
        cut.FindAll("#error-price_per_1k_input_usd").Should().BeEmpty(
            "both prices are nullable members");
        cut.FindAll("#model-error").Should().BeEmpty(
            "client-side failures are field errors, not the banner");

        ctx.Handler.Requests.Should().BeEmpty(
            "the required check runs BEFORE any network call (acceptance criterion)");
    }

    [Fact]
    public void EditModelDialog_InvalidBaseUrl_ShowsInlineError()
    {
        using var ctx = new SpaTestContext();
        var cut = ctx.RenderComponent<EditModelDialog>();

        FillValidForm(cut, baseUrl: "not-a-url");
        cut.Find("#model-save").Click();

        cut.Find("#error-base_url").TextContent.Should().Contain(
            "absolute http:// or https:// URL",
            "the task's base_url rule rejects a non-URI value");
        cut.Find("#error-base_url").GetAttribute("class").Should().Contain("validation-message");
        cut.FindAll("#model-error").Should().BeEmpty();
        cut.FindAll("#model-save").Should().NotBeEmpty("the dialog stays open for a correction");
        ctx.Handler.Requests.Should().BeEmpty();
    }

    // -----------------------------------------------------------------
    // Probe failure (acceptance criterion: excerpt in a red banner, open)
    // -----------------------------------------------------------------

    [Fact]
    public void EditModelDialog_ProbeFails_ShowsProbeExcerptInRedBanner()
    {
        using var ctx = new SpaTestContext();
        ctx.Handler.Responder = (request, _) => request.Method == HttpMethod.Post
            ? Task.FromResult(ProbeFailedProblem())
            : Task.FromResult(SpaTestContext.Json("{}"));

        var cut = ctx.RenderComponent<EditModelDialog>();
        FillValidForm(cut);

        cut.Find("#model-save").Click();

        cut.WaitForAssertion(() =>
        {
            var banner = cut.Find("#model-error");
            banner.GetAttribute("role").Should().Be("alert");
            banner.GetAttribute("class").Should().Contain("error-box",
                "the excerpt is rendered in the RED banner the task asks for");
            banner.TextContent.Should().StartWith("Probe failed: ");
            banner.TextContent.Should().Contain(
                "upstream returned 401 Unauthorized",
                "the RFC 7807 detail IS the probe's response excerpt (M3T3)");
        });

        cut.FindAll("#model-save").Should().NotBeEmpty(
            "the dialog stays open so base_url / model_name can be corrected");
        cut.FindAll("#error-base_url").Should().BeEmpty(
            "a failed probe is not a field-level schema error");
        cut.FindAll("#model-display-name").Should().NotBeEmpty();
    }

    // -----------------------------------------------------------------
    // Save
    // -----------------------------------------------------------------

    [Fact]
    public void EditModelDialog_Save_HappyPath_PostsToApi_AndClosesDialog_OnSuccess()
    {
        using var ctx = new SpaTestContext();
        ctx.Store.Set(SpaTestContext.FakeJwt(role: "Admin"));

        var created = false;
        ctx.Handler.Responder = (request, _) =>
        {
            if (request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath == "/v1/admin/models")
            {
                created = true;
                return Task.FromResult(
                    SpaTestContext.Json(CreatedJson, HttpStatusCode.Created));
            }

            return Task.FromResult(SpaTestContext.Json(created
                ? $$"""{"items":[{{CreatedJson}}],"next_cursor":null}"""
                : EmptyListJson));
        };

        var page = ctx.RenderComponent<ModelsPage>();
        page.WaitForAssertion(() => page.Find("#models-empty").Should().NotBeNull());

        page.Find("#add-model-open").Click();
        FillValidForm(page, baseUrl: "https://api.example.test/v1");
        page.Find("#model-price-input").Change("0.02");
        page.Find("#model-price-output").Change("0.01");
        page.Find("#model-save").Click();

        page.WaitForAssertion(() => page.FindAll("#model-display-name").Should().BeEmpty(
            "the parent closes the dialog on OnSaved"));
        page.WaitForAssertion(() => page.Markup.Should().Contain("mini chat",
            "the post-save reload shows the created row (acceptance criterion)"));

        var create = ctx.Handler.Requests.Should().ContainSingle(
                r => r.Method == "POST" && r.Path == "/v1/admin/models")
            .Which;
        create.Body.Should().Contain("\"display_name\":\"mini chat\"")
            .And.Contain("\"provider\":\"openai-compatible\"",
                "provider is written by the form, read-only on screen (SQ-M3-14)")
            .And.Contain("\"base_url\":\"https://api.example.test/v1\"")
            .And.Contain("\"model_name\":\"gpt-4o-mini\"")
            .And.Contain("\"api_key\":\"sk-test-candidate\"")
            .And.Contain("\"price_per_1k_input_usd\":0.02")
            .And.Contain("\"enabled\":true");
        page.Markup.Should().NotContain(CandidateKey,
            "the key never reaches the rendered page (task invariant)");

        ctx.Handler.Requests.Count(r => r.Path == "/v1/admin/models?limit=25")
            .Should().Be(2, "load on init plus the reload after save");
    }

    [Fact]
    public void EditModelDialog_Save_ProblemDetailsErrors_ShowsInline()
    {
        using var ctx = new SpaTestContext();
        ctx.Handler.Responder = (request, _) => request.Method == HttpMethod.Post
            ? Task.FromResult(ConfigProblem())
            : Task.FromResult(SpaTestContext.Json("{}"));

        var cut = ctx.RenderComponent<EditModelDialog>();
        FillValidForm(cut);

        cut.Find("#model-save").Click();

        cut.WaitForAssertion(() =>
            cut.Find("#error-base_url").TextContent.Should().Contain("must be an absolute URI"));
        cut.Find("#error-model_name").TextContent.Should().Contain("is not a known model");
        cut.Find("#error-base_url").GetAttribute("class").Should().Contain("validation-message");
        cut.FindAll("#model-error").Should().BeEmpty(
            "both errors name known fields, so nothing falls back to the banner");
        cut.FindAll("#model-display-name").Should().NotBeEmpty(
            "a 400 keeps the dialog open so the form can be corrected");
    }

    /// <summary>
    /// Task Risks "PATCH semantics on api_key": a blank password field means
    /// "keep the stored credential", which on the wire is the ABSENCE of the
    /// member — never an empty string the server could store as a key.
    /// </summary>
    [Fact]
    public void EditModelDialog_Patch_LeavesApiKeyBlank_KeepsExisting()
    {
        using var ctx = new SpaTestContext();
        ctx.Handler.Responder = (request, _) =>
            request.RequestUri!.AbsolutePath == $"/v1/admin/models/{ModelId}"
                ? Task.FromResult(SpaTestContext.Json(ModelJson))
                : Task.FromResult(SpaTestContext.Json("{}"));

        var saved = false;
        var cut = ctx.RenderComponent<EditModelDialog>(parameters => parameters
            .Add(dialog => dialog.ModelId, ModelId)
            .Add(dialog => dialog.OnCancel, EventCallback.Factory.Create(ctx, static () => { }))
            .Add(dialog => dialog.OnSaved, EventCallback.Factory.Create<ModelDto>(
                ctx, _ => saved = true)));

        cut.WaitForAssertion(() => cut.Find("#model-display-name")
            .GetAttribute("value").Should().Be("mini chat"));
        cut.Find("#model-base-url").GetAttribute("value").Should().Be("https://api.example.test/v1");
        cut.Find("#model-model-name").GetAttribute("value").Should().Be("gpt-4o-mini");
        cut.Find("#model-enabled").HasAttribute("checked").Should().BeTrue();
        cut.Find("#model-api-key").GetAttribute("value").Should().BeNullOrEmpty(
            "the API never returns a key, so edit starts blank");
        cut.Find("#model-api-key-note").TextContent.Should()
            .Contain("Leave blank to keep the stored key.");

        cut.Find("#model-display-name").Change("renamed chat");
        cut.Find("#model-save").Click();

        cut.WaitForAssertion(() => saved.Should().BeTrue(
            "OnSaved hands the updated projection back to the parent"));
        var patch = ctx.Handler.Requests.Should().ContainSingle(
                r => r.Method == "PATCH")
            .Which;
        patch.Path.Should().Be($"/v1/admin/models/{ModelId}");
        patch.Body.Should().Contain("\"display_name\":\"renamed chat\"")
            .And.Contain("\"base_url\":\"https://api.example.test/v1\"")
            .And.Contain("\"enabled\":true")
            .And.NotContain("\"api_key\"",
                "a blank field is ABSENT from the body — the stored key survives");
        cut.FindAll("#model-error").Should().BeEmpty();
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    /// <summary>Fills every required field (create-mode defaults elsewhere).</summary>
    private static void FillValidForm<TComponent>(
        IRenderedComponent<TComponent> cut,
        string displayName = "mini chat",
        string baseUrl = "https://api.example.test/v1",
        string apiKey = CandidateKey)
        where TComponent : IComponent
    {
        cut.Find("#model-display-name").Change(displayName);
        cut.Find("#model-base-url").Change(baseUrl);
        cut.Find("#model-model-name").Change("gpt-4o-mini");
        cut.Find("#model-api-key").Change(apiKey);
    }

    /// <summary>The M3T3 400 /errors/model-probe-failed document (detail = the excerpt).</summary>
    private static HttpResponseMessage ProbeFailedProblem() => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent(
            """
            {"type":"/errors/model-probe-failed","title":"Model probe failed","status":400,"detail":"upstream returned 401 Unauthorized from https://api.example.test/v1"}
            """,
            Encoding.UTF8,
            "application/problem+json"),
    };

    /// <summary>An RFC 7807 400 naming two fields, exactly as ConfigErrorMapper writes it.</summary>
    private static HttpResponseMessage ConfigProblem() => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent(
            """
            {"type":"/errors/config-validation","title":"Config validation failed","status":400,"detail":"The model configuration failed the engine schema validation.","errors":[{"name":"base_url","reason":"must be an absolute URI"},{"name":"model_name","reason":"is not a known model."}]}
            """,
            Encoding.UTF8,
            "application/problem+json"),
    };
}
