using System.Net;
using Bunit;
using EmailAgent.Spa.Pages.Account;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Spa;

/// <summary>
/// M1T8 bUnit coverage for the account screens: client-side validation, the
/// form-encoded login contract, first-login routing and RFC 7807 error
/// rendering, the Q-A4 two-input passphrase UX, and the context-sensitive
/// change-password shape (task test list — AccountPagesTests).
/// </summary>
public class AccountPagesTests
{
    // -----------------------------------------------------------------
    // Login
    // -----------------------------------------------------------------

    [Fact]
    public void Login_Form_EmptyEmail_ShowsValidationError()
    {
        using var ctx = new SpaTestContext();
        var cut = ctx.RenderComponent<Login>();

        cut.Find("button[type=submit]").Click();

        var messages = cut.FindAll(".validation-message")
            .Select(element => element.TextContent)
            .ToList();
        messages.Should().Contain(message => message.Contains("Email"),
            "an empty email must fail client-side validation before any API call");
        ctx.Handler.Requests.Should().BeEmpty(
            "validation failure must never reach the API");
        ctx.Store.AccessToken.Should().BeNull();
    }

    [Fact]
    public void Login_Form_Valid_Submits_AndRoutesOnSuccess()
    {
        using var ctx = new SpaTestContext();
        ctx.Handler.Responder = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/account/login")
            {
                var token = SpaTestContext.FakeJwt(role: "Admin");
                return Task.FromResult(SpaTestContext.Json(
                    $"{{\"access_token\":\"{token}\",\"expires_in\":900,\"must_complete_first_login\":false}}"));
            }

            return Task.FromResult(SpaTestContext.Json("{}"));
        };

        var cut = ctx.RenderComponent<Login>();
        cut.Find("#email").Change("admin@example.test");
        cut.Find("#password").Change("Sup3rSecret!pass");
        cut.Find("button[type=submit]").Click();

        cut.WaitForAssertion(() => ctx.Store.AccessToken.Should().NotBeNull(
            "a 200 login must populate the in-memory token store"));

        var login = ctx.Handler.Requests.Should().ContainSingle(
                r => r.Path == "/v1/account/login")
            .Which;
        login.Method.Should().Be("POST");
        login.Body.Should().NotBeNull("login posts an application/x-www-form-urlencoded body")
            .And.Contain("email=admin%40example.test")
            .And.Contain("password=Sup3rSecret%21pass",
                "'!' is percent-encoded by FormUrlEncodedContent per the form encoding");

        // Admin role in the token ⇒ /admin/users (task step 10 routing).
        ctx.Nav.Uri.Should().Be("http://localhost/admin/users");
    }

    /// <summary>
    /// M2T6 review fix (round 1 CRITICAL, pinned here for the primary path):
    /// a sign-in is a new principal — the DEK cached by a previous session
    /// must not survive into this one (it would be sent as X-User-Dek for
    /// another account's sources, and no re-wrap path exists in M2).
    /// </summary>
    [Fact]
    public void Login_Form_Success_DropsPreCachedDek()
    {
        using var ctx = new SpaTestContext();
        ctx.Store.Set(SpaTestContext.FakeJwt(role: "User"));
        ctx.Store.DekStore.SetDek("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");
        ctx.Handler.Responder = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/account/login")
            {
                var token = SpaTestContext.FakeJwt(role: "User");
                return Task.FromResult(SpaTestContext.Json(
                    $"{{\"access_token\":\"{token}\",\"expires_in\":900,\"must_complete_first_login\":false}}"));
            }

            return Task.FromResult(SpaTestContext.Json("{}"));
        };

        var cut = ctx.RenderComponent<Login>();
        cut.Find("#email").Change("alice@example.test");
        cut.Find("#password").Change("Sup3rSecret!pass");
        cut.Find("button[type=submit]").Click();

        cut.WaitForAssertion(() => ctx.Store.AccessToken.Should().NotBeNull(
            "the new session's token replaced the old one"));

        ctx.Store.DekStore.IsEmpty.Should().BeTrue(
            "a new principal must never inherit the previous session's key");
        ctx.Store.DekStore.DekBase64.Should().BeNull();
    }

    [Fact]
    public void Login_Form_InvalidCredentials_ShowsProblemDetails()
    {
        using var ctx = new SpaTestContext();
        ctx.Handler.Responder = (request, _) => request.RequestUri!.AbsolutePath == "/v1/account/login"
            ? Task.FromResult(SpaTestContext.Problem(
                HttpStatusCode.Unauthorized, "Invalid email or password."))
            : Task.FromResult(SpaTestContext.Json("{}"));

        var cut = ctx.RenderComponent<Login>();
        cut.Find("#email").Change("admin@example.test");
        cut.Find("#password").Change("wrong-password-1!");
        cut.Find("button[type=submit]").Click();

        cut.WaitForAssertion(() =>
            cut.Find(".error-box").TextContent.Should().Contain("Invalid email or password.",
                "the RFC 7807 detail from the 401 is shown to the operator"));
        ctx.Store.AccessToken.Should().BeNull(
            "a failed login must not populate the token store");
        ctx.Nav.Uri.Should().Be("http://localhost/",
            "no routing happens on bad credentials");
    }

    /// <summary>
    /// The OTP sign-in path is a sign-in too: it must drop a pre-cached DEK
    /// exactly like the password path (M2T6 round-1 CRITICAL, both callers).
    /// </summary>
    [Fact]
    public void OtpLogin_Form_Success_DropsPreCachedDek()
    {
        using var ctx = new SpaTestContext();
        ctx.Store.Set(SpaTestContext.FakeJwt(role: "User"));
        ctx.Store.DekStore.SetDek("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");
        ctx.Handler.Responder = (request, _) =>
            request.RequestUri!.AbsolutePath == "/v1/account/otp-login"
                ? Task.FromResult(SpaTestContext.Json(
                    $"{{\"access_token\":\"{SpaTestContext.FakeJwt(role: "User")}\",\"expires_in\":900,\"must_complete_first_login\":true}}"))
                : Task.FromResult(SpaTestContext.Json("{}"));

        var cut = ctx.RenderComponent<OtpLogin>();
        cut.Find("#otp-email").Change("alice@example.test");
        cut.Find("#otp").Change("123456");
        cut.Find("button[type=submit]").Click();

        cut.WaitForAssertion(() => ctx.Store.AccessToken.Should().NotBeNull(
            "the OTP exchange stored the new access token"));

        ctx.Store.DekStore.IsEmpty.Should().BeTrue(
            "an OTP sign-in is a new principal as well — no inherited key");
        ctx.Nav.Uri.Should().Be("http://localhost/account/change-password",
            "the OTP flow always continues into the first-login passphrase setup (Q-E6)");
    }

    // -----------------------------------------------------------------
    // SetPassphrase (Q-A4 / Q-E1: two inputs, confirm, ≥ 12 chars)
    // -----------------------------------------------------------------

    [Fact]
    public void SetPassphrase_TwoInputs_Mismatch_ShowsError()
    {
        using var ctx = new SpaTestContext();
        var cut = ctx.RenderComponent<SetPassphrase>();

        cut.Find("#passphrase").Change("correct horse battery staple");
        cut.Find("#passphrase-confirm").Change("correct horse battery differe");
        cut.Find("button[type=submit]").Click();

        var messages = cut.FindAll(".validation-message")
            .Select(element => element.TextContent)
            .ToList();
        messages.Should().Contain(message => message.Contains("do not match"),
            "the two passphrase inputs must agree before submit (Q-A4)");
        ctx.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void SetPassphrase_TwoInputs_Valid_Submits()
    {
        using var ctx = new SpaTestContext();
        ctx.Handler.Responder = (request, _) => request.RequestUri!.AbsolutePath == "/v1/account/set-passphrase"
            ? Task.FromResult(SpaTestContext.NoContent())
            : Task.FromResult(SpaTestContext.Json("{}"));

        var cut = ctx.RenderComponent<SetPassphrase>();
        cut.Find("#passphrase").Change("correct horse battery staple");
        cut.Find("#passphrase-confirm").Change("correct horse battery staple");
        cut.Find("button[type=submit]").Click();

        cut.WaitForAssertion(() => ctx.Nav.Uri.Should().Be(
            "http://localhost/", "a 204 set-passphrase routes to the landing page"));

        var post = ctx.Handler.Requests.Should().ContainSingle(
                r => r.Path == "/v1/account/set-passphrase")
            .Which;
        post.Method.Should().Be("POST");
        post.Body.Should().NotBeNull()
            .And.Contain("\"passphrase\":\"correct horse battery staple\"")
            .And.Contain("\"passphrase_confirm\":\"correct horse battery staple\"");
    }

    // -----------------------------------------------------------------
    // ChangePassword (first-login vs subsequent flow shapes)
    // -----------------------------------------------------------------

    [Fact]
    public void ChangePassword_FirstLogin_RendersTwoInputs_NoOldPassword()
    {
        using var ctx = new SpaTestContext();
        var cut = ctx.RenderComponent<ChangePassword>();

        cut.FindAll("input").Should().HaveCount(2,
            "the first-login shape is new password + confirmation only");
        cut.FindAll("#old-password").Should().BeEmpty(
            "the session is the credential on first login — no old-password field");
    }

    [Fact]
    public void ChangePassword_SubsequentLogin_RendersThreeInputs_WithOldPassword()
    {
        using var ctx = new SpaTestContext();
        var cut = ctx.RenderComponent<ChangePassword>(
            parameters => parameters.Add(component => component.FirstLogin, false));

        cut.FindAll("input").Should().HaveCount(3,
            "the subsequent shape adds the current-password field");
        cut.FindAll("#old-password").Should().HaveCount(1);
    }
}
