using System.Net;
using Bunit;
using EmailAgent.Spa.Pages.Admin;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Spa;

/// <summary>
/// M1T8 bUnit coverage for the admin user list: role-gated rendering, the
/// non-admin redirect, the create dialog → view-once OTP modal handoff, and
/// Q-A3's client-side view-once enforcement (a dismissed OTP never re-renders
/// on the next mount) — task test list (AdminUserListTests).
/// </summary>
public class AdminUserListTests
{
    /// <summary>The fixed OTP value reused across create responses in these tests.</summary>
    private const string Otp = "JBSWY3DPEHPK3PXP";

    private const string UserListJson =
        "{\"items\":[{\"id\":\"22222222-2222-2222-2222-222222222222\","
        + "\"email\":\"alice@example.test\",\"role\":\"User\",\"status\":\"Active\","
        + "\"must_change_password\":false,\"created_at\":\"2026-01-01T00:00:00+00:00\","
        + "\"last_login_at\":null}],\"next_cursor\":null}";

    private const string CreateUserOkJson =
        "{\"id\":\"33333333-3333-3333-3333-333333333333\","
        + "\"email\":\"bob@example.test\",\"role\":\"User\",\"status\":\"Pending\","
        + "\"otp\":\"" + Otp + "\",\"otp_expires_at\":\"2026-01-08T00:00:00+00:00\"}";

    [Fact]
    public void AdminUserList_AdminRole_ShowsTable()
    {
        using var ctx = new SpaTestContext();
        ctx.Store.Set(SpaTestContext.FakeJwt(role: "Admin"));
        ctx.Handler.Responder = (request, _) => request.RequestUri!.PathAndQuery
            .StartsWith("/v1/admin/users", StringComparison.Ordinal)
            ? Task.FromResult(SpaTestContext.Json(UserListJson))
            : Task.FromResult(SpaTestContext.Json("{}"));

        var cut = ctx.RenderComponent<Users>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("alice@example.test"));

        var headers = cut.FindAll("th").Select(th => th.TextContent).ToList();
        headers.Should().Contain("Email").And.Contain("Role").And.Contain("Status")
            .And.Contain("MustChangePassword").And.Contain("CreatedAt")
            .And.Contain("LastLoginAt",
                "the table renders the §13.13 projection's seven columns");

        ctx.Handler.Requests.Should().ContainSingle(
            r => r.Path.StartsWith("/v1/admin/users", StringComparison.Ordinal));
    }

    [Fact]
    public void AdminUserList_NonAdminRole_RedirectsToLogin()
    {
        using var ctx = new SpaTestContext();
        ctx.Store.Set(SpaTestContext.FakeJwt(role: "User"));

        var cut = ctx.RenderComponent<Users>();

        cut.WaitForAssertion(() => ctx.Nav.Uri.Should().Be(
            "http://localhost/account/login",
            "a non-admin token must never reach the user list"));
        ctx.Handler.Requests.Should().BeEmpty(
            "the client-side role gate fires before any API call");
    }

    [Fact]
    public void AdminUserList_CreateUser_DialogReturns_ShowsViewOnceOtpModal()
    {
        using var ctx = new SpaTestContext();
        ctx.Store.Set(SpaTestContext.FakeJwt(role: "Admin"));
        ctx.Handler.Responder = (request, _) =>
        {
            var method = request.Method;
            var path = request.RequestUri!.PathAndQuery;
            if (method == HttpMethod.Post && path == "/v1/admin/users")
            {
                return Task.FromResult(SpaTestContext.Json(CreateUserOkJson, HttpStatusCode.Created));
            }

            return Task.FromResult(path.StartsWith("/v1/admin/users", StringComparison.Ordinal)
                ? SpaTestContext.Json(UserListJson)
                : SpaTestContext.Json("{}"));
        };

        var cut = ctx.RenderComponent<Users>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("alice@example.test"));

        cut.Find("#create-user-open").Click();
        cut.Find("#create-email").Change("bob@example.test");
        cut.Find("#create-user-submit").Click();

        // Dialog closes; the 201 body's OTP renders exactly once (Q-A3).
        cut.WaitForAssertion(() =>
        {
            cut.FindAll("#create-user-submit").Should().BeEmpty("the dialog closes after 201");
            cut.Find("#view-once-otp").TextContent.Should().Be(Otp);
        });
        cut.Markup.Should().Contain("This code will not be shown again.");
        cut.Find("#otp-copy").TextContent.Should().Contain("Copy to clipboard");
        cut.Find("#otp-modal-close").TextContent.Should().Contain("Close");

        var create = ctx.Handler.Requests.Should().ContainSingle(
                r => r.Method == "POST" && r.Path == "/v1/admin/users")
            .Which;
        create.Body.Should().Contain("\"email\":\"bob@example.test\"")
            .And.Contain("\"role\":\"User\"");
    }

    [Fact]
    public void AdminUserList_ClosingViewOnceOtpModal_DoesNotReRender_Otp_OnNextMount()
    {
        using var ctx = new SpaTestContext();
        ctx.Store.Set(SpaTestContext.FakeJwt(role: "Admin"));
        ctx.Handler.Responder = (request, _) =>
        {
            var method = request.Method;
            var path = request.RequestUri!.PathAndQuery;
            if (method == HttpMethod.Post && path == "/v1/admin/users")
            {
                return Task.FromResult(SpaTestContext.Json(CreateUserOkJson, HttpStatusCode.Created));
            }

            return Task.FromResult(path.StartsWith("/v1/admin/users", StringComparison.Ordinal)
                ? SpaTestContext.Json(UserListJson)
                : SpaTestContext.Json("{}"));
        };

        // First mount: create a user, see the OTP, dismiss it.
        var first = ctx.RenderComponent<Users>();
        first.WaitForAssertion(() => first.Markup.Should().Contain("alice@example.test"));
        first.Find("#create-user-open").Click();
        first.Find("#create-email").Change("bob@example.test");
        first.Find("#create-user-submit").Click();
        first.WaitForAssertion(() => first.Find("#view-once-otp").TextContent.Should().Be(Otp));

        first.Find("#otp-modal-close").Click();
        first.WaitForAssertion(() => first.FindAll("#view-once-otp").Should().BeEmpty(
            "closing the modal removes it from the render tree"));
        first.Markup.Should().NotContain(Otp);

        // Second mount (navigate away and back — same session): the parent
        // re-presents the SAME OTP (worst case: a stubbed reissue), and the
        // view-once state must keep it suppressed (Q-A3).
        first.Dispose();
        var second = ctx.RenderComponent<Users>();
        second.WaitForAssertion(() => second.Markup.Should().Contain("alice@example.test"));
        second.Find("#create-user-open").Click();
        second.Find("#create-email").Change("bob@example.test");
        second.Find("#create-user-submit").Click();

        second.WaitForAssertion(() =>
            second.FindAll("#create-user-submit").Should().BeEmpty(
                "the create flow completed and the dialog closed"));
        second.Markup.Should().NotContain(Otp,
            "a dismissed OTP must never re-render, even if the parent supplies it again");
    }
}