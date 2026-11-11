using System.Net;
using Bunit;
using EmailAgent.Spa.Notifications;
using FluentAssertions;
using Xunit;

// The sources page lives in EmailAgent.Spa.Pages.User; it is aliased because
// the unit-test project's own namespace tree has EmailAgent.UnitTests.Sources.
using SourcesPage = EmailAgent.Spa.Pages.User.Sources;

namespace EmailAgent.UnitTests.Spa;

/// <summary>
/// M2T6 bUnit coverage for the /user/sources page (task test list): the
/// unlock gate (M2's DEK gate, M5T1's crypto-session gate), the empty and
/// table states, the add-dialog handoff, the milestone-pinned
/// <c>SourcesPage_TestConnection_BadPassword_ShowsInlineError</c> row test
/// (deviation option 3), fetch-now's info toast, the delete-confirm flow, the
/// defense-in-depth cross-user assertion and the 403-inline path — all over
/// <see cref="StubHttpHandler"/>, never a real service (AGENTS.md hard
/// constraint 3).
/// </summary>
public class SourcesPageTests
{
    private static readonly Guid SourceId = new("44444444-4444-4444-4444-444444444444");

    private const string EmptyListJson = """{"items":[],"next_cursor":null}""";

    private const string RowJson =
        """
        {"items":[{"id":"44444444-4444-4444-4444-444444444444","type_id":"imap","display_name":"work imap","status":"Active","interval_minutes":5,"checkpoint":{"last_uid":null,"extra":null},"last_run_at":"2026-01-02T10:30:00+00:00","next_run_at":null,"created_at":"2026-01-01T00:00:00+00:00","config":{"host":"imap.example.test","port":993,"use_ssl":true,"username":"alice","mailbox":"INBOX"}}],"next_cursor":null}
        """;

    private const string FetchJson =
        """{"processed":3,"new_processed_messages":2,"checkpoint":{"last_uid":"42","extra":null}}""";

    // -----------------------------------------------------------------
    // States
    // -----------------------------------------------------------------

    [Fact]
    public void SourcesPage_EmptyState_ShowsEmptyMessage_AndAddButton()
    {
        using var ctx = UnlockedContext();

        var cut = ctx.RenderComponent<SourcesPage>();

        cut.WaitForAssertion(() => cut.FindAll("#sources-empty")
            .Should().ContainSingle("an unlocked session with no sources shows the empty state"));
        cut.Markup.Should().Contain("No sources yet. Add one to start fetching mail.");
        cut.Find("#add-source-open").TextContent.Should().Be("Add source");
        cut.Find("#sources-reload").TextContent.Should().Be("Reload");
        cut.FindAll("#sources-table").Should().BeEmpty();

        ctx.Handler.Requests.Should().ContainSingle(
            r => r.Path == "/v1/user/sources?limit=25",
            "the list loads once, from GET /v1/user/sources");
    }

    [Fact]
    public void SourcesPage_WithRows_RendersTable_WithActionButtons()
    {
        using var ctx = UnlockedContext(RowJson);

        var cut = ctx.RenderComponent<SourcesPage>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("work imap"));

        var headers = cut.FindAll("th").Select(th => th.TextContent).ToList();
        headers.Should().HaveCount(7, "the task's seven-column table");
        headers.Should().Equal(
            "Display name", "Type", "Status", "Interval (min)",
            "Last run", "Next run", "Actions");

        var cells = cut.FindAll("tbody tr td").Select(td => td.TextContent).ToList();
        cells.Should().HaveCount(7, "one row × seven columns");
        cells[0].Should().Be("work imap");
        cells[1].Should().Be("imap");
        cells[2].Should().Be("Active");
        cells[3].Should().Be("5");
        cells[4].Should().NotBe("—", "last_run_at renders in the viewer's local time");
        cells[5].Should().Be("—", "next_run_at is null for a source that never ran");

        cut.Find($"#source-edit-{SourceId:N}").TextContent.Should().Be("Edit");
        cut.Find($"#source-test-{SourceId:N}").TextContent.Should().Be("Test connection");
        cut.Find($"#source-fetch-{SourceId:N}").TextContent.Should().Be("Fetch now");
        cut.Find($"#source-delete-{SourceId:N}").TextContent.Should().Be("Delete");
    }

    [Fact]
    public void SourcesPage_WithoutSession_ShowsPrompt_AndHidesTable()
    {
        using var ctx = new SpaTestContext();
        ctx.Store.Set(SpaTestContext.FakeJwt(role: "User"));

        var cut = ctx.RenderComponent<SourcesPage>();

        cut.WaitForAssertion(() => cut.Find("#unlock-submit").Should().NotBeNull(
            "an empty crypto-session slot presents the unlock prompt"));
        cut.Markup.Should().Contain("Unlock encrypted data");
        cut.FindAll("#sources-table").Should().BeEmpty(
            "the table stays out of the markup entirely until the session is unlocked");
        cut.FindAll("#sources-empty").Should().BeEmpty();
        ctx.Handler.Requests.Should().BeEmpty(
            "no list request is made before the unlock session exists");
    }

    [Fact]
    public void SourcesPage_WithSession_SkipsPrompt()
    {
        using var ctx = UnlockedContext(RowJson);

        var cut = ctx.RenderComponent<SourcesPage>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("work imap"));

        cut.FindAll("#unlock-submit").Should().BeEmpty(
            "a cached session token unlocks the page without re-prompting");
    }

    [Fact]
    public void SourcesPage_Forbidden_RendersInlineError_AndStaysOnPage()
    {
        using var ctx = UnlockedContext();
        ctx.Handler.Responder = (_, _) => Task.FromResult(SpaTestContext.Json(
            """
            {"type":"/errors/forbidden","title":"Forbidden","status":403,"detail":"This account is disabled."}
            """,
            HttpStatusCode.Forbidden));

        var cut = ctx.RenderComponent<SourcesPage>();

        cut.WaitForAssertion(() =>
        {
            var error = cut.Find("#sources-error");
            error.GetAttribute("role").Should().Be("alert");
            error.TextContent.Should().Contain("You are not allowed to view sources.");
        });
        ctx.Nav.Uri.Should().Be(
            "http://localhost/", "403 stays inline — only a 401 redirects to login");

        // Round 4 (NIT): a FAILED first load must not render the empty state
        // next to the banner — "No sources yet. Add one…" would invite a
        // duplicate create while the list is unknown.
        cut.FindAll("#sources-empty").Should().BeEmpty();
        cut.FindAll("#sources-table").Should().BeEmpty();
    }

    // -----------------------------------------------------------------
    // Add dialog handoff
    // -----------------------------------------------------------------

    [Fact]
    public void SourcesPage_AddButton_OpensEditDialog_WithEmptyForm()
    {
        using var ctx = UnlockedContext();

        var cut = ctx.RenderComponent<SourcesPage>();
        cut.WaitForAssertion(() => cut.Find("#add-source-open").Should().NotBeNull());

        cut.Find("#add-source-open").Click();

        cut.Find("#source-display-name").GetAttribute("value").Should().BeNullOrEmpty(
            "add mode starts with a blank display name");
        cut.Find("#source-interval").GetAttribute("value").Should().Be("5");
        cut.Find("#source-port").GetAttribute("value").Should().Be("993");
        cut.Find("#source-mailbox").GetAttribute("value").Should().Be("INBOX");
        cut.Find("#source-use-ssl").HasAttribute("checked").Should().BeTrue(
            "use_ssl defaults to true per ImapConfigSchema");
        cut.Find("#source-type").HasAttribute("readonly").Should().BeTrue(
            "the type is read-only: imap is the only M2 plugin");
        cut.Find("#source-password-note").TextContent.Should().Contain(
            "Required", "create mode requires a password (task scope bullet)");
        cut.Find("#source-interval-note").TextContent.Should()
            .Contain("Scheduling is enabled in M5.");

        ctx.Handler.Requests.Should().ContainSingle(
            r => r.Path == "/v1/user/sources?limit=25",
            "add mode never fetches an existing source (no GET /v1/user/sources/{id})");
    }

    // -----------------------------------------------------------------
    // Row-level connection test (deviation option 3)
    // -----------------------------------------------------------------

    [Fact]
    public void SourcesPage_TestConnection_BadPassword_ShowsInlineError()
    {
        using var ctx = new SpaTestContext();
        ctx.Store.Set(SpaTestContext.FakeJwt(role: "User"));
        ctx.Store.CryptoSession.SetToken(SpaTestContext.CryptoSessionToken);
        ctx.Handler.Responder = (request, _) =>
            request.Method == HttpMethod.Post
            && request.RequestUri!.AbsolutePath == "/v1/user/sources/test-connection"
                ? Task.FromResult(SpaTestContext.Json(
                    """{"ok":false,"error":"authentication failed: NO LOGIN failed for alice"}"""))
                : Task.FromResult(SpaTestContext.Json(RowJson));

        var cut = ctx.RenderComponent<SourcesPage>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("work imap"));

        cut.Find($"#source-test-{SourceId:N}").Click();
        cut.Find("#row-test-password").Change("definitely-not-the-password");
        cut.Find("#row-test-submit").Click();

        cut.WaitForAssertion(() =>
        {
            var error = cut.Find("#row-test-error");
            error.GetAttribute("role").Should().Be("alert");
            error.TextContent.Should().Contain(
                "authentication failed: NO LOGIN failed for alice",
                "the API's own error string is surfaced inline in the row panel");
        });

        // The panel stays open so the password can be retyped.
        cut.Find("#row-test-password").Should().NotBeNull();

        var post = ctx.Handler.Requests.Should().ContainSingle(
                r => r.Method == "POST"
                    && r.Path.EndsWith("/test-connection", StringComparison.Ordinal))
            .Which;
        post.Body.Should().Contain("\"type_id\":\"imap\"")
            .And.Contain("\"host\":\"imap.example.test\"",
                "the candidate config is the row projection plus the typed password")
            .And.Contain("definitely-not-the-password");
    }

    // -----------------------------------------------------------------
    // Fetch now
    // -----------------------------------------------------------------

    [Fact]
    public void SourcesPage_FetchNow_ReturnsCount_ShowsInfoToast()
    {
        using var ctx = UnlockedContext();
        ctx.Handler.Responder = (request, _) =>
            request.Method == HttpMethod.Post
            && request.RequestUri!.AbsolutePath.EndsWith("/fetch-now", StringComparison.Ordinal)
                ? Task.FromResult(SpaTestContext.Json(FetchJson))
                : Task.FromResult(SpaTestContext.Json(RowJson));

        var cut = ctx.RenderComponent<SourcesPage>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("work imap"));
        var toasts = ctx.RenderComponent<ToastContainer>();

        cut.Find($"#source-fetch-{SourceId:N}").Click();

        toasts.WaitForAssertion(() => toasts.Markup.Should().Contain(
            "Fetched 'work imap': 2 new message(s), 3 processed.",
            "the acceptance criterion pins an INFO toast carrying the counts"));
        ctx.Toasts.Items.Should().ContainSingle(t => t.Kind == ToastKind.Info);
        toasts.FindAll("[role=\"status\"]").Should().NotBeEmpty(
            "info toasts announce through the polite live region");
        cut.FindAll("#sources-error").Should().BeEmpty();
    }

    // -----------------------------------------------------------------
    // Delete
    // -----------------------------------------------------------------

    [Fact]
    public void SourcesPage_Delete_OpensConfirmModal_AndRemovesRow_OnConfirm()
    {
        using var ctx = new SpaTestContext();
        ctx.Store.Set(SpaTestContext.FakeJwt(role: "User"));
        ctx.Store.CryptoSession.SetToken(SpaTestContext.CryptoSessionToken);
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

        var cut = ctx.RenderComponent<SourcesPage>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("work imap"));
        var toasts = ctx.RenderComponent<ToastContainer>();

        cut.Find($"#source-delete-{SourceId:N}").Click();
        cut.Find("#delete-source-title").TextContent.Should().Be("Delete source");
        cut.Markup.Should().Contain("This cannot be undone.");

        cut.Find("#delete-source-confirm").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("#delete-source-title").Should().BeEmpty(
                "the modal closes after the API accepts the delete");
            cut.Find("#sources-empty").Should().NotBeNull(
                "the post-delete reload now returns an empty page");
        });
        ctx.Handler.Requests.Should().ContainSingle(
            r => r.Method == "DELETE"
                && r.Path == $"/v1/user/sources/{SourceId}");
        toasts.WaitForAssertion(() => ctx.Toasts.Items.Should().ContainSingle(
            t => t.Kind == ToastKind.Success
                && t.Message.Contains("Source 'work imap' deleted.")));
    }

    [Fact]
    public void SourcesPage_Delete_OtherUsersRow_NotVisible()
    {
        // Server-side contract: GET /v1/user/sources returns ONLY the caller's
        // rows (SourcesEndpointsTests.Sources_List_ReturnsOnlyCallersSources_
        // AndRateLimitHeaders) and a foreign id answers 404, never 403
        // (Sources_CrossUser_Requests_Return404_Never403). The SPA has no
        // merge or cache path either — it renders exactly the items of the
        // page it received, which is the defense this test pins.
        using var ctx = UnlockedContext(RowJson);

        var cut = ctx.RenderComponent<SourcesPage>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("work imap"));

        cut.FindAll("tbody tr").Should().HaveCount(1);
        cut.Markup.Should().NotContain(
            "55555555-5555-5555-5555-555555555555",
            "no foreign source id can appear: nothing in the SPA fabricates or caches rows");
    }

    /// <summary>
    /// M2T6 review fix, carried into M5T1: a <c>/errors/dek-invalid</c> answer
    /// means the session's key cannot open this account's blobs — the page
    /// drops the session token and falls back to the unlock prompt instead of
    /// a dead end.
    /// </summary>
    [Fact]
    public void SourcesPage_DekInvalid_DropsKey_AndRePrompts()
    {
        using var ctx = UnlockedContext();
        ctx.Handler.Responder = (_, _) => Task.FromResult(SpaTestContext.Json(
            """
            {"type":"/errors/dek-invalid","title":"Bad Request","status":400,"detail":"the stored DEK does not decrypt this source's config blob."}
            """,
            HttpStatusCode.BadRequest));

        var cut = ctx.RenderComponent<SourcesPage>();

        cut.WaitForAssertion(() => cut.Find("#unlock-submit").Should().NotBeNull(
            "an unusable cached key re-presents the passphrase prompt"));
        cut.FindAll("#sources-table").Should().BeEmpty();
        cut.FindAll("#sources-empty").Should().BeEmpty(
            "the table stays hidden while the session is locked again");

        // M2T6 review fix (round 3, NIT): the re-lock must explain itself —
        // the locked branch has no table, so the banner carries the reason.
        var banner = cut.Find("#sources-error");
        banner.GetAttribute("role").Should().Be("alert");
        banner.TextContent.Should().Contain(
            "Enter your passphrase again",
            "a re-prompt with no explanation reads as a silent logout");

        ctx.Store.CryptoSession.IsEmpty.Should().BeTrue(
            "the session token that failed is dropped from memory");
    }

    /// <summary>
    /// M5T1: the session gate's own verdict — 403 <c>/errors/crypto-locked</c>
    /// for a missing/expired/revoked <c>X-Crypto-Session</c> — re-locks with
    /// the same banner as a rejected key. (The M2T6 round-4
    /// <c>/errors/validation</c> arm naming <c>x-user-dek</c> retired with the
    /// header.)
    /// </summary>
    [Fact]
    public void SourcesPage_CryptoLocked_ReLocks()
    {
        using var ctx = UnlockedContext();
        ctx.Handler.Responder = (_, _) => Task.FromResult(SpaTestContext.Json(
            """
            {"type":"/errors/crypto-locked","title":"Forbidden","status":403,"detail":"No active crypto session."}
            """,
            HttpStatusCode.Forbidden));

        var cut = ctx.RenderComponent<SourcesPage>();

        cut.WaitForAssertion(() => cut.Find("#unlock-submit").Should().NotBeNull(
            "an expired or revoked session re-locks instead of leaving an inline dead end"));
        cut.Find("#sources-error").TextContent.Should().Contain("Enter your passphrase again");
        cut.FindAll("#sources-table").Should().BeEmpty();
        cut.FindAll("#sources-empty").Should().BeEmpty(
            "no empty state next to a failure — the first page never arrived");
        ctx.Store.CryptoSession.IsEmpty.Should().BeTrue(
            "the rejected session token is cleared");
    }

    /// <summary>
    /// M2T6 review fix (round 3, WARNING), carried into M5T1: the store is
    /// the source of truth for the unlock gate. A sign-in elsewhere in the
    /// browser clears the shared <c>CryptoSession</c> while this page still
    /// renders as unlocked — the next action must re-lock with a banner
    /// instead of firing requests that go out with a stale session token.
    /// </summary>
    [Fact]
    public void SourcesPage_SignInElsewhere_EmptiedSession_RelocksOnNextAction()
    {
        using var ctx = UnlockedContext(RowJson);

        var cut = ctx.RenderComponent<SourcesPage>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("work imap"));

        // Another tab signs in: TokenStore.SignIn drops the cached session
        // token while THIS page still holds _unlocked = true.
        ctx.Store.SignIn(SpaTestContext.FakeJwt(role: "User"));
        ctx.Store.CryptoSession.IsEmpty.Should().BeTrue();

        cut.Find("#sources-reload").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find("#unlock-submit").Should().NotBeNull(
                "the page falls back to the prompt instead of erroring forever");
            cut.Find("#sources-error").TextContent.Should().Contain(
                "Enter your passphrase again");
        });
        cut.FindAll("#sources-table").Should().BeEmpty();
        ctx.Handler.Requests.Should().ContainSingle(
            r => r.Path == "/v1/user/sources?limit=25",
            "only the FIRST load ran — the store check stops the reloaded one");
    }

    // -----------------------------------------------------------------
    // Cursor paging (round 3, NIT): the first keyset page is not the list
    // -----------------------------------------------------------------

    /// <summary>First keyset page: one row plus a cursor to the next page.</summary>
    private const string FirstPageJson =
        """
        {"items":[{"id":"44444444-4444-4444-4444-444444444444","type_id":"imap","display_name":"work imap","status":"Active","interval_minutes":5,"created_at":"2026-01-01T00:00:00+00:00"}],"next_cursor":"cursor-1"}
        """;

    /// <summary>Second (final) keyset page: a different row, no further cursor.</summary>
    private const string SecondPageJson =
        """
        {"items":[{"id":"55555555-5555-5555-5555-555555555555","type_id":"imap","display_name":"second imap","status":"Active","interval_minutes":15,"created_at":"2026-01-02T00:00:00+00:00"}],"next_cursor":null}
        """;

    [Fact]
    public void SourcesPage_LoadMore_AppendsNextPage_AndDisappearsOnLastPage()
    {
        using var ctx = UnlockedContext(FirstPageJson);
        ctx.Handler.Responder = (request, _) => Task.FromResult(SpaTestContext.Json(
            request.RequestUri!.Query.Contains("cursor=", StringComparison.Ordinal)
                ? SecondPageJson
                : FirstPageJson));

        var cut = ctx.RenderComponent<SourcesPage>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("work imap"));
        cut.Find("#sources-load-more").TextContent.Should().Be("Load more");

        cut.Find("#sources-load-more").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("second imap"));
        cut.FindAll("tbody tr").Should().HaveCount(2,
            "the second keyset page is appended — nothing past limit=25 is hidden");
        ctx.Handler.Requests.Should().Contain(
            r => r.Path.Contains("cursor=cursor-1", StringComparison.Ordinal),
            "the next page is requested with the cursor the server handed out");
        cut.FindAll("#sources-load-more").Should().BeEmpty(
            "next_cursor = null on the last page — the control retires");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    /// <summary>An unlocked (token + session) context whose list endpoint answers <paramref name="listJson"/>.</summary>
    private static SpaTestContext UnlockedContext(string listJson = EmptyListJson)
    {
        var ctx = new SpaTestContext();
        ctx.Store.Set(SpaTestContext.FakeJwt(role: "User"));
        ctx.Store.CryptoSession.SetToken(SpaTestContext.CryptoSessionToken);
        ctx.Handler.Responder = (_, _) => Task.FromResult(SpaTestContext.Json(listJson));
        return ctx;
    }
}
