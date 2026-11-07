using System.Net;
using System.Text;
using Bunit;
using EmailAgent.Spa.Pages.Admin;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace EmailAgent.UnitTests.Spa;

/// <summary>
/// M3T5 bUnit coverage for the test-completion dialog (task test list): the
/// happy path rendering response text, tokens and the six-decimal cost, the
/// `—` rendering a null <c>cost_usd</c> (and null token counts), and an RFC
/// 7807 refusal surfacing in the red banner with the dialog still open.
/// </summary>
public class TestCompletionDialogTests
{
    private static readonly Guid ModelId = new("44444444-4444-4444-4444-444444444444");

    private const string TestPath = "/v1/admin/models/44444444-4444-4444-4444-444444444444/test-completion";

    [Fact]
    public void TestCompletionDialog_HappyPath_ShowsResponseTextTokensAndCost()
    {
        using var ctx = new SpaTestContext();
        ctx.Handler.Responder = (request, _) => request.Method == HttpMethod.Post
            && request.RequestUri!.AbsolutePath.EndsWith(
                "/test-completion", StringComparison.Ordinal)
                ? Task.FromResult(SpaTestContext.Json(
                    """
                    {"response_text":"hello world","tool_calls":null,"prompt_tokens":5,"completion_tokens":2,"total_tokens":7,"cost_usd":0.0007,"duration_ms":12}
                    """))
                : Task.FromResult(SpaTestContext.Json("{}"));

        var cut = ctx.RenderComponent<TestCompletionDialog>(parameters => parameters
            .Add(dialog => dialog.ModelId, ModelId)
            .Add(dialog => dialog.ModelName, "gpt-4o-mini")
            .Add(dialog => dialog.OnClose, EventCallback.Factory.Create(
                ctx, static () => { })));

        cut.Find("#test-system-prompt").Change("You are helpful.");
        cut.Find("#test-user-prompt").Change("Say hello.");
        cut.Find("#test-run").Click();

        cut.WaitForAssertion(() => cut.Find("#test-response").Should().NotBeNull(
            "a 200 renders the response section (never a toast)"));
        cut.Find("#test-response-text").TextContent.Should().Be("hello world");
        var tokens = cut.Find("#test-tokens").TextContent;
        tokens.Should().Contain("prompt: 5").And.Contain("completion: 2")
            .And.Contain("total: 7");
        cut.Find("#test-cost").TextContent.Should().Contain(
            "0.000700", "the cost is formatted to six decimals (task acceptance)");
        cut.Find("#test-duration").TextContent.Should().Contain("12");
        cut.FindAll("#test-tool-calls").Should().BeEmpty(
            "tool_calls: null renders no tool list");
        cut.FindAll("#test-error").Should().BeEmpty();

        var post = ctx.Handler.Requests.Should().ContainSingle(
                r => r.Method == "POST" && r.Path == TestPath)
            .Which;
        post.Body.Should().Contain("\"system_prompt\":\"You are helpful.\"")
            .And.Contain("\"user_prompt\":\"Say hello.\"")
            .And.NotContain("\"tools\"",
                "no tool rows were added, so the member is omitted entirely");
    }

    [Fact]
    public void TestCompletionDialog_CostNull_ShowsDash()
    {
        using var ctx = new SpaTestContext();
        ctx.Handler.Responder = (_, _) => Task.FromResult(SpaTestContext.Json(
            """
            {"response_text":"done","tool_calls":null,"prompt_tokens":null,"completion_tokens":null,"total_tokens":0,"cost_usd":null,"duration_ms":3}
            """));

        var cut = RenderDialog(ctx);
        cut.Find("#test-user-prompt").Change("Say hello.");
        cut.Find("#test-run").Click();

        cut.WaitForAssertion(() => cut.Find("#test-cost").TextContent.Should().Be(
            "Cost (USD): —", "a null cost renders as the em dash, never 0"));
        var tokens = cut.Find("#test-tokens").TextContent;
        tokens.Should().Contain("prompt: —",
                "a provider that reported no usage surfaces JSON nulls, not a fabricated 0")
            .And.Contain("completion: —")
            .And.Contain("total: 0");
        cut.Find("#test-response-text").TextContent.Should().Be("done");
    }

    [Fact]
    public void TestCompletionDialog_Run_HttpError_ShowsProblemDetails()
    {
        using var ctx = new SpaTestContext();
        ctx.Handler.Responder = (request, _) => request.Method == HttpMethod.Post
            ? Task.FromResult(RefusalProblem())
            : Task.FromResult(SpaTestContext.Json("{}"));

        var cut = RenderDialog(ctx);
        cut.Find("#test-user-prompt").Change("Say hello.");
        cut.Find("#test-run").Click();

        cut.WaitForAssertion(() =>
        {
            var error = cut.Find("#test-error");
            error.GetAttribute("role").Should().Be("alert");
            error.TextContent.Should().Contain(
                "The model configuration failed the engine schema validation.",
                "the RFC 7807 detail is rendered, not a bare status code");
            error.TextContent.Should().Contain("model_name");
        });

        cut.FindAll("#test-response").Should().BeEmpty(
            "a refusal never fabricates a response section");
        cut.Find("#test-run").Should().NotBeNull(
            "the dialog stays open so the prompt can be corrected");
        cut.Markup.Should().Contain("Say hello.",
            "the typed prompt is kept for the next attempt");

        ctx.Handler.Requests.Should().ContainSingle(
            r => r.Method == "POST" && r.Path == TestPath);
    }

    /// <summary>
    /// Review WARNING fix: a refused second run must not leave the first
    /// run's response/cost block on screen — a success result beside an error
    /// banner would read as the outcome of the prompt just submitted.
    /// </summary>
    [Fact]
    public void TestCompletionDialog_SecondRunFails_ClearsPreviousResponse()
    {
        using var ctx = new SpaTestContext();
        ctx.Handler.Responder = (_, _) => Task.FromResult(SpaTestContext.Json(
            """
            {"response_text":"hello world","tool_calls":null,"prompt_tokens":5,"completion_tokens":2,"total_tokens":7,"cost_usd":0.0007,"duration_ms":12}
            """));

        var cut = RenderDialog(ctx);
        cut.Find("#test-user-prompt").Change("Say hello.");
        cut.Find("#test-run").Click();
        cut.WaitForAssertion(() => cut.Find("#test-response").Should().NotBeNull());

        ctx.Handler.Responder = (request, _) => request.Method == HttpMethod.Post
            ? Task.FromResult(RefusalProblem())
            : Task.FromResult(SpaTestContext.Json("{}"));
        cut.Find("#test-run").Click();

        cut.WaitForAssertion(() => cut.Find("#test-error").Should().NotBeNull(
            "the refusal renders its banner"));
        cut.FindAll("#test-response").Should().BeEmpty(
            "the stale first result never survives a new run");
        cut.FindAll("#test-response-text").Should().BeEmpty();
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    /// <summary>Mounts the dialog over the model id with a no-op close handler.</summary>
    private static IRenderedComponent<TestCompletionDialog> RenderDialog(SpaTestContext ctx)
        => ctx.RenderComponent<TestCompletionDialog>(parameters => parameters
            .Add(dialog => dialog.ModelId, ModelId)
            .Add(dialog => dialog.ModelName, "gpt-4o-mini")
            .Add(dialog => dialog.OnClose, EventCallback.Factory.Create(
                ctx, static () => { })));

    /// <summary>An RFC 7807 400 naming a field the dialog does not own — it belongs in the banner.</summary>
    private static HttpResponseMessage RefusalProblem() => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent(
            """
            {"type":"/errors/config-validation","title":"Config validation failed","status":400,"detail":"The model configuration failed the engine schema validation.","errors":[{"name":"model_name","reason":"is not a known model."}]}
            """,
            Encoding.UTF8,
            "application/problem+json"),
    };
}
