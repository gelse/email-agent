using System.Net;
using Bunit;
using EmailAgent.Spa.Pages.User;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace EmailAgent.UnitTests.Spa;

/// <summary>
/// M2T6 (adjudication R2) bUnit coverage for the unlock prompt: the
/// passphrase goes to <c>POST /v1/user/account/unwrap-dek</c> and the
/// returned key lands in <c>TokenStore.DekStore</c> — no JS crypto, no
/// <c>IJSRuntime</c>. Each terminal outcome of the endpoint (200 / 401 / 409
/// / 429) and the client-side required check have their own fact.
/// </summary>
public class PassphrasePromptTests
{
    private const string Dek = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    private const string Passphrase = "correct horse battery staple";

    private const string UnwrapPath = "/v1/user/account/unwrap-dek";

    [Fact]
    public void PassphrasePrompt_ValidPassphrase_PostsToUnwrapDek_AndStoresDek()
    {
        using var ctx = new SpaTestContext();
        ctx.Handler.Responder = (request, _) => request.RequestUri!.AbsolutePath == UnwrapPath
            ? Task.FromResult(SpaTestContext.Json($"{{\"dek\":\"{Dek}\"}}"))
            : Task.FromResult(SpaTestContext.Json("{}"));

        var unlocked = false;
        var cut = ctx.RenderComponent<PassphrasePrompt>(parameters => parameters
            .Add(prompt => prompt.OnUnlocked, EventCallback.Factory.Create(
                ctx, () => unlocked = true)));

        cut.Find("#unlock-passphrase").Change(Passphrase);
        cut.Find("#unlock-submit").Click();

        cut.WaitForAssertion(() => unlocked.Should().BeTrue(
            "a 200 unwrap hands the key to the page and closes the prompt"));
        ctx.Store.DekStore.DekBase64.Should().Be(Dek,
            "the DEK is cached in memory only — the session's X-User-Dek source");

        var post = ctx.Handler.Requests.Should().ContainSingle(
            r => r.Path == UnwrapPath).Which;
        post.Method.Should().Be("POST");
        post.Body.Should().Contain($"\"passphrase\":\"{Passphrase}\"",
            "the passphrase travels exactly once, in the request body");
        cut.FindAll("#unlock-error").Should().BeEmpty();
        cut.Find("#unlock-passphrase").GetAttribute("value").Should().BeNullOrEmpty(
            "the passphrase field is cleared once the key is cached");
    }

    [Fact]
    public void PassphrasePrompt_WrongPassphrase_401_ShowsInlineError_AndClearsField()
    {
        using var ctx = new SpaTestContext();
        ctx.Handler.Responder = (request, _) => request.RequestUri!.AbsolutePath == UnwrapPath
            ? Task.FromResult(SpaTestContext.Json(
                """
                {"type":"/errors/credential-mismatch","title":"Unauthorized","status":401,"detail":"The passphrase is not valid."}
                """,
                HttpStatusCode.Unauthorized))
            : Task.FromResult(SpaTestContext.Json("{}"));

        var unlocked = false;
        var cut = ctx.RenderComponent<PassphrasePrompt>(parameters => parameters
            .Add(prompt => prompt.OnUnlocked, EventCallback.Factory.Create(
                ctx, () => unlocked = true)));

        cut.Find("#unlock-passphrase").Change("definitely-wrong");
        cut.Find("#unlock-submit").Click();

        cut.WaitForAssertion(() =>
        {
            var error = cut.Find("#unlock-error");
            error.GetAttribute("role").Should().Be("alert");
            error.TextContent.Should().Be("The passphrase is not valid.");
        });
        unlocked.Should().BeFalse("a failed unwrap never unlocks the page");
        ctx.Store.DekStore.IsEmpty.Should().BeTrue();
        cut.FindAll("#unlock-not-set").Should().BeEmpty();
        cut.Find("#unlock-passphrase").GetAttribute("value").Should().BeNullOrEmpty(
            "a typo is retyped, not resubmitted blindly");
    }

    [Fact]
    public void PassphrasePrompt_NoPassphraseSet_409_ShowsFirstLoginLink()
    {
        using var ctx = new SpaTestContext();
        ctx.Handler.Responder = (request, _) => request.RequestUri!.AbsolutePath == UnwrapPath
            ? Task.FromResult(SpaTestContext.Json(
                """
                {"type":"/errors/passphrase-not-set","title":"Conflict","status":409,"detail":"No passphrase has been set for this account, so no wrapped data-encryption key exists yet. Complete the first-login passphrase flow."}
                """,
                HttpStatusCode.Conflict))
            : Task.FromResult(SpaTestContext.Json("{}"));

        var cut = ctx.RenderComponent<PassphrasePrompt>();

        cut.Find("#unlock-passphrase").Change(Passphrase);
        cut.Find("#unlock-submit").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find("#unlock-not-set").Should().NotBeNull(
                "409 routes the user into the first-login passphrase flow");
            cut.Find("#unlock-set-passphrase").GetAttribute("href")
                .Should().Be("/account/set-passphrase");
        });
        cut.Find("#unlock-error").TextContent.Should()
            .Contain("No passphrase has been set");
        ctx.Store.DekStore.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void PassphrasePrompt_RateLimited_429_ShowsRetryMessage()
    {
        using var ctx = new SpaTestContext();
        ctx.Handler.Responder = (request, _) => request.RequestUri!.AbsolutePath == UnwrapPath
            ? Task.FromResult(SpaTestContext.Json(
                """
                {"type":"/errors/rate-limited","title":"Too Many Requests","status":429,"detail":"A key derivation for this account is already in progress; retry shortly."}
                """,
                HttpStatusCode.TooManyRequests))
            : Task.FromResult(SpaTestContext.Json("{}"));

        var cut = ctx.RenderComponent<PassphrasePrompt>();

        cut.Find("#unlock-passphrase").Change(Passphrase);
        cut.Find("#unlock-submit").Click();

        cut.WaitForAssertion(() => cut.Find("#unlock-error").TextContent.Should()
            .Contain("Key derivation already in progress — try again shortly."));
        ctx.Store.DekStore.IsEmpty.Should().BeTrue();
        ctx.Handler.Requests.Should().ContainSingle(r => r.Path == UnwrapPath);
    }

    [Fact]
    public void PassphrasePrompt_EmptyPassphrase_ClientValidation_BlocksRequest()
    {
        using var ctx = new SpaTestContext();
        var cut = ctx.RenderComponent<PassphrasePrompt>();

        cut.Find("#unlock-submit").Click();

        cut.FindAll(".validation-message")
            .Select(element => element.TextContent)
            .Should().Contain(message => message.Contains("The passphrase is required."),
                "the required check runs before any request");
        ctx.Handler.Requests.Should().BeEmpty();
    }

    /// <summary>
    /// M2T6 review fix (round 3, WARNING): a 401 that is not a credential
    /// verdict means the SESSION never made it (expired access token, refresh
    /// cookie gone or revoked in another tab) — the passphrase was never
    /// judged, so blaming it would send the user in circles retyping a
    /// correct secret. The prompt routes to sign-in instead.
    /// </summary>
    [Fact]
    public void PassphrasePrompt_Session401_RedirectsToLogin_InsteadOfBlamingPassphrase()
    {
        using var ctx = new SpaTestContext();
        ctx.Handler.Responder = (request, _) => request.RequestUri!.AbsolutePath == UnwrapPath
            ? Task.FromResult(SpaTestContext.Json(
                """
                {"type":"/errors/unauthorized","title":"Unauthorized","status":401,"detail":"The access token is missing or expired."}
                """,
                HttpStatusCode.Unauthorized))
            : Task.FromResult(SpaTestContext.Json("{}"));

        var cut = ctx.RenderComponent<PassphrasePrompt>();

        cut.Find("#unlock-passphrase").Change(Passphrase);
        cut.Find("#unlock-submit").Click();

        cut.WaitForAssertion(() => ctx.Nav.Uri.Should().Be(
            "http://localhost/account/login",
            "a session failure is fixed by signing in, not by retyping the passphrase"));
        cut.FindAll("#unlock-error").Should().BeEmpty(
            "no 'passphrase is not valid' message: nothing evaluated it");
        ctx.Store.DekStore.IsEmpty.Should().BeTrue();
    }

    /// <summary>
    /// M2T6 review fix (round 2): the typed passphrase leaves the field on
    /// EVERY terminal outcome — a transport failure used to leave it rendered
    /// in the DOM, because the clear only happened on HTTP answers.
    /// </summary>
    [Fact]
    public void PassphrasePrompt_TransportFailure_ShowsBanner_AndClearsField()
    {
        using var ctx = new SpaTestContext();
        ctx.Handler.Responder = (request, _) => request.RequestUri!.AbsolutePath == UnwrapPath
            ? throw new HttpRequestException("connection reset")
            : Task.FromResult(SpaTestContext.Json("{}"));

        var cut = ctx.RenderComponent<PassphrasePrompt>();

        cut.Find("#unlock-passphrase").Change(Passphrase);
        cut.Find("#unlock-submit").Click();

        cut.WaitForAssertion(() =>
        {
            var error = cut.Find("#unlock-error");
            error.GetAttribute("role").Should().Be("alert");
            error.TextContent.Should().Contain("Could not reach the API.");
        });
        ctx.Store.DekStore.IsEmpty.Should().BeTrue();
        cut.Find("#unlock-passphrase").GetAttribute("value").Should().BeNullOrEmpty(
            "a failed network attempt must not leave the passphrase in the DOM");
        ctx.Handler.Requests.Should().ContainSingle(r => r.Path == UnwrapPath,
            "the POST was attempted before the transport failed");
    }
}
