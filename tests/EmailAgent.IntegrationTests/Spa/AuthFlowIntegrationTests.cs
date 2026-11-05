using System.Net;
using System.Text;
using Bunit;
using EmailAgent.IntegrationTests.Endpoints;
using EmailAgent.Spa.Auth;
using EmailAgent.Spa.Pages;
using EmailAgent.Spa.Pages.Account;
using EmailAgent.Spa.Pages.Admin;
using EmailAgent.Core.Identity;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmailAgent.IntegrationTests.Spa;

/// <summary>
/// M1T8 task step 17: drives the full first-login flow through the real SPA
/// pages (bUnit) against the real API host (WebApplicationFactory +
/// Testcontainers Postgres) — admin login → admin creates user → view-once
/// OTP modal → user OTP-login → forced change-password → set-passphrase →
/// dashboard stub → logout — asserting request bodies, the token store, the
/// refresh cookie's attributes, silent refresh after a simulated reload, and
/// the post-logout cookie clear. The transport is the REAL
/// <see cref="BearerTokenHandler"/> + <see cref="RefreshCookieAuthenticator"/>
/// over a cookie jar that mimics a browser (same-origin, Secure cookies are
/// honoured on http://localhost exactly as a browser's secure-context
/// allowance does).
/// </summary>
[Collection("Postgres")]
public class AuthFlowIntegrationTests
{
    /// <summary>Same-origin dev origin (nginx serves SPA + API — §13.9).</summary>
    private static readonly Uri BaseAddress = new("http://localhost:8081/");

    private readonly PostgresFixture _postgres;

    public AuthFlowIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task AuthFlow_Login_CreateUser_OtpLogin_ChangePassword_SetPassphrase_Dashboard_Logout()
    {
        var connectionString = await AccountTestHost.MigrateAsync(_postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        await AdminTestHost.EnsureRolesAsync(factory);

        var adminEmail = $"spa-admin-{Guid.NewGuid():N}@example.test";
        var newUserEmail = $"spa-alice-{Guid.NewGuid():N}@example.test";
        await SeedAdminAsync(factory, adminEmail);

        // Real browser-equivalent transport: cookie jar + the production
        // bearer handler chain (401 → one silent refresh → retry).
        using var jar = new CookieJarHandler { InnerHandler = factory.Server.CreateHandler() };
        var store = new TokenStore();
        var refreshHttp = new HttpClient(jar) { BaseAddress = BaseAddress };
        var authenticator = new RefreshCookieAuthenticator(
            new FixedClientFactory(refreshHttp), store);
        var apiHttp = new HttpClient(new BearerTokenHandler(store, authenticator)
        {
            InnerHandler = jar,
        })
        {
            BaseAddress = BaseAddress,
        };

        using var flow = new FlowContext(apiHttp, store);
        var refreshToken = authenticator; // local alias for readability below

        // ---- 1. Admin logs in (form-encoded) and lands on /admin/users ----
        var loginPage = flow.RenderComponent<Login>();
        loginPage.Find("#email").Change(adminEmail);
        loginPage.Find("#password").Change(AccountTestHost.TempPassword);
        loginPage.Find("button[type=submit]").Click();

        loginPage.WaitForAssertion(
            () => flow.Store.AccessToken.Should().NotBeNullOrWhiteSpace(
                "a 200 login must populate the in-memory token store"),
            TimeSpan.FromSeconds(10));
        loginPage.WaitForAssertion(
            () => flow.Nav.Uri.Should().Be("http://localhost/admin/users",
                "the seeded admin's token carries the Admin role and no first-login flag"),
            TimeSpan.FromSeconds(10));

        var loginCall = jar.Calls.Should().ContainSingle(c => c.Path == "/v1/account/login").Which;
        loginCall.Status.Should().Be(HttpStatusCode.OK);
        loginCall.Body.Should().Contain("email=").And.Contain("password=");
        loginCall.SetCookie.Should().NotBeNull("login sets the ea_refresh cookie");
        loginCall.SetCookie.Should().MatchRegex("(?i)path=/v1/account/refresh")
            .And.MatchRegex("(?i)httponly")
            .And.MatchRegex("(?i)secure")
            .And.MatchRegex("(?i)samesite=lax",
                "DoD: HttpOnly, Secure, SameSite=Lax, Path=/v1/account/refresh");
        jar.RefreshCookie.Should().NotBeNullOrWhiteSpace();
        var adminToken = flow.Store.AccessToken;

        // ---- 2. Admin user list renders against the real /v1/admin/users ----
        var usersPage = flow.RenderComponent<Users>();
        usersPage.WaitForAssertion(
            () => usersPage.Markup.Should().Contain(adminEmail),
            TimeSpan.FromSeconds(10));

        // ---- 3. Create user dialog → 201 → view-once OTP modal ----
        usersPage.Find("#create-user-open").Click();
        usersPage.Find("#create-email").Change(newUserEmail);
        usersPage.Find("#create-user-submit").Click();

        usersPage.WaitForAssertion(
            () => usersPage.Find("#view-once-otp").TextContent.Trim().Should().HaveLength(16,
                "the create response's OTP renders in the view-once modal"),
            TimeSpan.FromSeconds(10));
        var otp = usersPage.Find("#view-once-otp").TextContent.Trim();
        usersPage.Markup.Should().Contain("This code will not be shown again.");

        var createCall = jar.Calls.Should().ContainSingle(
                c => c.Method == "POST" && c.Path == "/v1/admin/users")
            .Which;
        createCall.Body.Should().Contain(newUserEmail).And.Contain("\"role\":\"User\"");
        createCall.Status.Should().Be(HttpStatusCode.Created);

        // ---- 4. Closing the modal permanently hides this OTP (Q-A3) ----
        usersPage.Find("#otp-modal-close").Click();
        usersPage.WaitForAssertion(
            () => usersPage.FindAll("#view-once-otp").Should().BeEmpty(),
            TimeSpan.FromSeconds(10));
        usersPage.Markup.Should().NotContain(otp);
        usersPage.Dispose();

        // ---- 5. The new user OTP-logs in (first-login flow forced) ----
        var otpLoginPage = flow.RenderComponent<OtpLogin>();
        otpLoginPage.Find("#otp-email").Change(newUserEmail);
        otpLoginPage.Find("#otp").Change(otp);
        otpLoginPage.Find("button[type=submit]").Click();

        otpLoginPage.WaitForAssertion(
            () => flow.Nav.Uri.Should().Be("http://localhost/account/change-password",
                "otp-login always routes into the forced first-login flow"),
            TimeSpan.FromSeconds(10));
        flow.Store.AccessToken.Should().NotBeNullOrWhiteSpace();
        flow.Store.AccessToken.Should().NotBe(adminToken,
            "the OTP session replaced the admin session in the token store");

        var otpLoginCall = jar.Calls.Should().ContainSingle(
                c => c.Path == "/v1/account/otp-login")
            .Which;
        otpLoginCall.Body.Should().Contain("email=").And.Contain($"otp={otp}");

        // ---- 6. Forced change-password (two inputs, no old_password) ----
        var changePage = flow.RenderComponent<ChangePassword>();
        changePage.FindAll("#old-password").Should().BeEmpty();
        changePage.Find("#new-password").Change(AccountTestHost.NewPassword);
        changePage.Find("#new-password-confirm").Change(AccountTestHost.NewPassword);
        changePage.Find("button[type=submit]").Click();

        changePage.WaitForAssertion(
            () => flow.Nav.Uri.Should().Be("http://localhost/account/set-passphrase",
                "the session flag says key material is missing — set-passphrase is next"),
            TimeSpan.FromSeconds(10));
        var changeCall = jar.Calls.Should().ContainSingle(
                c => c.Path == "/v1/account/change-password")
            .Which;
        changeCall.Body.Should().Contain("new_password")
            .And.NotContain("old_password",
                "the first-login shape never sends old_password");

        // ---- 7. Two-input set-passphrase (Q-A4 / Q-E1) ----
        var passphrasePage = flow.RenderComponent<SetPassphrase>();
        passphrasePage.Find("#passphrase").Change(AccountTestHost.Passphrase);
        passphrasePage.Find("#passphrase-confirm").Change(AccountTestHost.Passphrase);
        passphrasePage.Find("button[type=submit]").Click();

        passphrasePage.WaitForAssertion(
            () => flow.Nav.Uri.Should().Be("http://localhost/",
                "a 204 set-passphrase routes to the landing page"),
            TimeSpan.FromSeconds(10));
        var setPassCall = jar.Calls.Should().ContainSingle(
                c => c.Path == "/v1/account/set-passphrase")
            .Which;
        setPassCall.Status.Should().Be(HttpStatusCode.NoContent);
        setPassCall.Body.Should().Contain("passphrase").And.Contain("passphrase_confirm");

        // ---- 8. Landing route → dashboard stub ----
        var homePage = flow.RenderComponent<Home>();
        homePage.WaitForAssertion(
            () => flow.Nav.Uri.Should().Be("http://localhost/dashboard",
                "a non-admin lands on the M1 stub dashboard"),
            TimeSpan.FromSeconds(10));
        var dashboard = flow.RenderComponent<StubDashboard>();
        dashboard.Markup.Should().Contain("M1 complete");

        // ---- 9. Simulated reload: token gone, silent refresh restores it ----
        store.Clear();
        store.AccessToken.Should().BeNull("Clear() empties the in-memory store (§13.10)");
        var refreshed = await refreshToken.AttemptSilentRefreshAsync();
        refreshed.Should().BeTrue("the httpOnly refresh cookie is still valid — the session survives F5");
        store.AccessToken.Should().NotBeNullOrWhiteSpace();

        // ---- 10. Logout: API call, token cleared, cookie expired ----
        var logoutPage = flow.RenderComponent<Logout>();
        logoutPage.WaitForAssertion(
            () => flow.Nav.Uri.Should().Be("http://localhost/account/login"),
            TimeSpan.FromSeconds(10));
        flow.Store.AccessToken.Should().BeNull("logout clears the token store");
        var logoutCall = jar.Calls.Should().ContainSingle(c => c.Path == "/v1/account/logout").Which;
        logoutCall.SetCookie.Should().NotBeNull("logout rewrites the cookie as an expiry");
        logoutCall.SetCookie.Should().MatchRegex("(?i)ea_refresh=").And.MatchRegex(
            "(?i)max-age=0", "the ea_refresh cookie is expired immediately (Max-Age=0)");
        jar.RefreshCookie.Should().BeNull("the jar drops the cleared cookie (browser parity)");

        // ---- 11. Without the cookie, a later silent refresh fails cleanly ----
        var afterLogout = await refreshToken.AttemptSilentRefreshAsync();
        afterLogout.Should().BeFalse("no cookie → no session (the SPA routes to /account/login)");
        store.AccessToken.Should().BeNull();
    }

    /// <summary>
    /// Seeds an Active Admin with a real Identity password, role membership
    /// (JWT role claim) and dummy key material so the admin's first login is
    /// not a forced first-login flow.
    /// </summary>
    private static async Task SeedAdminAsync(
        WebApplicationFactory<Program> factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserName = email,
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            MustChangePassword = false,
            CreatedAt = DateTimeOffset.UtcNow,
            KdfSalt = new byte[16],
            KdfParams = System.Text.Json.JsonSerializer.SerializeToDocument(
                EmailAgent.Core.Security.KdfParams.Default()),
            DekWrapped = new byte[48],
            DekNonce = new byte[12],
        };

        var created = await userManager.CreateAsync(user, AccountTestHost.TempPassword);
        created.Succeeded.Should().BeTrue(
            "seeding must succeed: " + string.Join(", ", created.Errors.Select(e => e.Description)));
        var roleResult = await userManager.AddToRoleAsync(user, "Admin");
        roleResult.Succeeded.Should().BeTrue(
            "the Admin role row must exist (AdminTestHost.EnsureRolesAsync) and the membership "
            + "must link so the JWT carries the role claim: "
            + string.Join(", ", roleResult.Errors.Select(e => e.Description)));
    }

    /// <summary>
    /// bUnit context for the flow: the production HttpClient (real bearer
    /// handler + cookie jar), the singleton token store instance shared with
    /// the handler chain, plus the parser and view-once state.
    /// </summary>
    private sealed class FlowContext : TestContext
    {
        public FlowContext(HttpClient api, TokenStore store)
        {
            Services.AddSingleton(store);
            Services.AddSingleton(api);
            Services.AddSingleton<JwtRoleParser>();
            Services.AddScoped<OtpModalState>();
        }

        public TokenStore Store => Services.GetRequiredService<TokenStore>();

        public Microsoft.AspNetCore.Components.NavigationManager Nav
            => Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
    }

    /// <summary>IHttpClientFactory returning the one configured client (refresh path).</summary>
    private sealed class FixedClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    /// <summary>
    /// Browser-equivalent cookie jar: stores the raw <c>ea_refresh</c> value
    /// from Set-Cookie, re-attaches it to every request (Secure ignored —
    /// localhost is a secure context), drops it on expiry (Max-Age=0), and
    /// records path/method/body/status/Set-Cookie per call for assertions.
    /// </summary>
    private sealed class CookieJarHandler : DelegatingHandler
    {
        public sealed record Call(
            string Method,
            string Path,
            string? Body,
            HttpStatusCode Status,
            string? SetCookie);

        private readonly List<Call> _calls = new();

        public IReadOnlyList<Call> Calls => _calls;

        public string? RefreshCookie { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (RefreshCookie is not null)
            {
                request.Headers.TryAddWithoutValidation(
                    "Cookie", $"ea_refresh={RefreshCookie}");
            }

            // Browsers attach Origin to non-GET requests — the API's refresh-
            // path Origin gate (M1T4) only accepts same-origin, so mirroring
            // the header exercises that gate on every silent refresh here
            // (Host in the TestServer equals this origin, port included).
            request.Headers.TryAddWithoutValidation(
                "Origin", BaseAddress.GetLeftPart(UriPartial.Authority));

            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            var response = await base.SendAsync(request, cancellationToken);

            var setCookie = JoinSetCookie(response);
            ApplyRefreshCookie(setCookie);

            _calls.Add(new Call(
                request.Method.Method,
                request.RequestUri?.PathAndQuery ?? "/",
                body,
                response.StatusCode,
                setCookie));
            return response;
        }

        private static string? JoinSetCookie(HttpResponseMessage response)
            => response.Headers.TryGetValues("Set-Cookie", out var values)
                ? string.Join("\n", values)
                : null;

        private void ApplyRefreshCookie(string? setCookie)
        {
            if (setCookie is null
                || !setCookie.Contains("ea_refresh=", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            const string prefix = "ea_refresh=";
            var start = setCookie.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            var raw = setCookie[(start + prefix.Length)..];
            var end = raw.IndexOfAny(new[] { ';', '\n' });
            var value = end < 0 ? raw : raw[..end];

            var lower = setCookie.ToLowerInvariant();
            var cleared = value.Length == 0
                || lower.Contains("max-age=0")
                || lower.Contains("1970");
            RefreshCookie = cleared ? null : value;
        }
    }
}