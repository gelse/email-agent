using System.Net;
using System.Text;
using Bunit;
using EmailAgent.Spa.Notifications;
using EmailAgent.Spa.Pages.User;
using EmailAgent.Spa.Sources;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Xunit;

// The sources page lives in EmailAgent.Spa.Pages.User; it is aliased because
// the unit-test project's own namespace tree has EmailAgent.UnitTests.Sources.
using SourcesPage = EmailAgent.Spa.Pages.User.Sources;

namespace EmailAgent.UnitTests.Spa;

/// <summary>
/// M2T6 bUnit coverage for the hand-written add/edit dialog (task test list,
/// Q-F4): the introspector-backed client-side required check (no network on a
/// form the schema already rejects), the bad-password inline banner and the
/// success toast of "Test connection", RFC 7807 <c>errors[]</c> rendered next
/// to the named field, the create → close → reload handoff through the parent
/// page, and edit mode's blank-password PATCH (task Risks "Password field UX
/// on edit").
/// </summary>
public class EditSourceDialogTests
{
    private static readonly Guid SourceId = new("44444444-4444-4444-4444-444444444444");

    private static readonly Guid CreatedId = new("66666666-6666-6666-6666-666666666666");

    private const string SourceJson =
        """
        {"id":"44444444-4444-4444-4444-444444444444","type_id":"imap","display_name":"work imap","status":"Active","interval_minutes":5,"checkpoint":{"last_uid":null,"extra":null},"last_run_at":null,"next_run_at":null,"created_at":"2026-01-01T00:00:00+00:00","config":{"host":"imap.example.test","port":993,"use_ssl":true,"username":"alice","mailbox":"INBOX"}}
        """;

    private const string CreatedJson =
        """
        {"id":"66666666-6666-6666-6666-666666666666","type_id":"imap","display_name":"New work imap","status":"Active","interval_minutes":5,"checkpoint":{"last_uid":null,"extra":null},"last_run_at":null,"next_run_at":null,"created_at":"2026-01-01T00:00:00+00:00","config":{"host":"imap.example.test","port":993,"use_ssl":true,"username":"alice","mailbox":"INBOX"}}
        """;

    private const string EmptyListJson = """{"items":[],"next_cursor":null}""";

    private const string CandidatePassword = "candidate-pass";

    // -----------------------------------------------------------------
    // Client-side validation
    // -----------------------------------------------------------------

    [Fact]
    public void EditSourceDialog_RequiredFields_ClientSideValidation()
    {
        using var ctx = new SpaTestContext();
        var cut = ctx.RenderComponent<EditSourceDialog>();

        cut.Find("#source-save").Click();

        cut.Find("#error-display_name").TextContent.Should().Be("Display name is required.");
        cut.Find("#error-host").TextContent.Should().Be("Host is required.");
        cut.Find("#error-username").TextContent.Should().Be("Username is required.");
        cut.Find("#error-password").TextContent.Should().Be("Password is required.");
        cut.FindAll("#error-port").Should().BeEmpty("port has a valid default");
        cut.FindAll("#error-interval_minutes").Should().BeEmpty("interval defaults to 5 (Q-O4)");
        cut.FindAll("#error-mailbox").Should().BeEmpty("mailbox defaults to INBOX");
        cut.FindAll("#source-error").Should().BeEmpty(
            "client-side failures are field errors, not the banner");

        ctx.Handler.Requests.Should().BeEmpty(
            "the required check runs BEFORE any network call (acceptance criterion)");
    }

    // -----------------------------------------------------------------
    // Test connection
    // -----------------------------------------------------------------

    [Fact]
    public void EditSourceDialog_TestConnection_BadPassword_ShowsInlineError()
    {
        using var ctx = new SpaTestContext();
        ctx.Handler.Responder = (request, _) =>
            request.RequestUri!.AbsolutePath == "/v1/user/sources/test-connection"
                ? Task.FromResult(SpaTestContext.Json(
                    """{"ok":false,"error":"authentication failed: NO LOGIN failed for alice"}"""))
                : Task.FromResult(SpaTestContext.Json("{}"));

        var cut = ctx.RenderComponent<EditSourceDialog>();
        FillValidForm(cut);

        cut.Find("#source-test").Click();

        cut.WaitForAssertion(() =>
        {
            var error = cut.Find("#source-test-error");
            error.GetAttribute("role").Should().Be("alert");
            error.TextContent.Should().Be("authentication failed: NO LOGIN failed for alice",
                "the plugin's own error string renders inline, not as a status code");
        });
        cut.FindAll("#source-error").Should().BeEmpty();

        var post = ctx.Handler.Requests.Should().ContainSingle(
                r => r.Method == "POST"
                    && r.Path == "/v1/user/sources/test-connection")
            .Which;
        post.Body.Should().Contain("\"type_id\":\"imap\"")
            .And.Contain("\"host\":\"imap.example.test\"")
            .And.Contain(CandidatePassword,
                "the schema requires a candidate password even when an edit keeps the stored one");
    }

    [Fact]
    public void EditSourceDialog_TestConnection_Success_ShowsSuccessToast()
    {
        using var ctx = new SpaTestContext();
        ctx.Handler.Responder = (request, _) =>
            request.RequestUri!.AbsolutePath == "/v1/user/sources/test-connection"
                ? Task.FromResult(SpaTestContext.Json("""{"ok":true,"error":null}"""))
                : Task.FromResult(SpaTestContext.Json("{}"));

        var cut = ctx.RenderComponent<EditSourceDialog>();
        FillValidForm(cut);
        var toasts = ctx.RenderComponent<ToastContainer>();

        cut.Find("#source-test").Click();

        toasts.WaitForAssertion(() => toasts.Markup.Should().Contain(
            "Connection to 'Greenmail inbox' succeeded."));
        ctx.Toasts.Items.Should().ContainSingle(
            t => t.Kind == ToastKind.Success
                && t.Message == "Connection to 'Greenmail inbox' succeeded.");
        cut.FindAll("#source-test-error").Should().BeEmpty(
            "a success outcome clears the inline banner");
    }

    // -----------------------------------------------------------------
    // Save
    // -----------------------------------------------------------------

    [Fact]
    public void EditSourceDialog_Save_PostsToApi_AndClosesDialog_OnSuccess()
    {
        using var ctx = new SpaTestContext();
        ctx.Store.Set(SpaTestContext.FakeJwt(role: "User"));
        ctx.Store.DekStore.SetDek("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");

        var created = false;
        ctx.Handler.Responder = (request, _) =>
        {
            if (request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath == "/v1/user/sources")
            {
                created = true;
                return Task.FromResult(
                    SpaTestContext.Json(CreatedJson, HttpStatusCode.Created));
            }

            return Task.FromResult(SpaTestContext.Json(created
                ? $$"""{"items":[{{CreatedJson}}],"next_cursor":null}"""
                : EmptyListJson));
        };

        var page = ctx.RenderComponent<SourcesPage>();
        page.WaitForAssertion(() => page.Find("#sources-empty").Should().NotBeNull());

        page.Find("#add-source-open").Click();
        page.Find("#source-display-name").Change("New work imap");
        page.Find("#source-host").Change("imap.example.test");
        page.Find("#source-username").Change("alice");
        page.Find("#source-password").Change("s3cret-pass");
        page.Find("#source-save").Click();

        page.WaitForAssertion(() => page.FindAll("#source-display-name").Should().BeEmpty(
            "the parent closes the dialog on OnSaved"));
        page.WaitForAssertion(() => page.Markup.Should().Contain("New work imap",
            "the post-save reload shows the created row (acceptance criterion)"));

        var create = ctx.Handler.Requests.Should().ContainSingle(
                r => r.Method == "POST" && r.Path == "/v1/user/sources")
            .Which;
        create.Body.Should().Contain("\"type_id\":\"imap\"")
            .And.Contain("\"display_name\":\"New work imap\"")
            .And.Contain("\"interval_minutes\":5")
            .And.Contain("\"password\":\"s3cret-pass\"")
            .And.Contain("\"use_ssl\":true");

        ctx.Handler.Requests.Count(r => r.Path == "/v1/user/sources?limit=25")
            .Should().Be(2, "load on init plus the reload after save");
    }

    [Fact]
    public void EditSourceDialog_Save_BadConfig_ShowsProblemDetailsErrors_Inline()
    {
        using var ctx = new SpaTestContext();
        ctx.Handler.Responder = (request, _) => request.Method == HttpMethod.Post
            ? Task.FromResult(ConfigProblem())
            : Task.FromResult(SpaTestContext.Json("{}"));

        var cut = ctx.RenderComponent<EditSourceDialog>();
        FillValidForm(cut);

        cut.Find("#source-save").Click();

        cut.WaitForAssertion(() => cut.Find("#error-port").TextContent
            .Should().Contain("must be between 1 and 65535"));
        cut.Find("#error-port").GetAttribute("class").Should().Contain("validation-message");
        cut.FindAll("#source-error").Should().BeEmpty(
            "the error names a known field, so it lands inline instead of the banner");
        cut.FindAll("#source-display-name").Should().NotBeEmpty(
            "a 400 keeps the dialog open so the form can be corrected");
    }

    [Fact]
    public void EditSourceDialog_EditMode_Prefills_AndBlankPassword_PatchesWithoutReplacing()
    {
        using var ctx = new SpaTestContext();
        ctx.Handler.Responder = (request, _) =>
            request.RequestUri!.AbsolutePath == $"/v1/user/sources/{SourceId}"
                ? Task.FromResult(SpaTestContext.Json(SourceJson))
                : Task.FromResult(SpaTestContext.Json("{}"));

        var saved = false;
        var cut = ctx.RenderComponent<EditSourceDialog>(parameters => parameters
            .Add(dialog => dialog.SourceId, SourceId)
            .Add(dialog => dialog.OnCancel, EventCallback.Factory.Create(ctx, static () => { }))
            .Add(dialog => dialog.OnSaved, EventCallback.Factory.Create<SourceDto>(
                ctx, _ => saved = true)));

        // Pre-fill from GET …/{id} — the projection carries no password.
        cut.WaitForAssertion(() => cut.Find("#source-display-name")
            .GetAttribute("value").Should().Be("work imap"));
        cut.Find("#source-host").GetAttribute("value").Should().Be("imap.example.test");
        cut.Find("#source-username").GetAttribute("value").Should().Be("alice");
        cut.Find("#source-mailbox").GetAttribute("value").Should().Be("INBOX");
        cut.Find("#source-password").GetAttribute("value").Should().BeNullOrEmpty(
            "the API never returns a password, so edit starts blank");
        cut.Find("#source-password-note").TextContent.Should()
            .Contain("Leave blank to keep the stored password.");

        cut.Find("#source-display-name").Change("renamed imap");
        cut.Find("#source-save").Click();

        cut.WaitForAssertion(() => saved.Should().BeTrue(
            "OnSaved hands the updated projection back to the parent"));
        var patch = ctx.Handler.Requests.Should().ContainSingle(
                r => r.Method == "PATCH")
            .Which;
        patch.Path.Should().Be($"/v1/user/sources/{SourceId}");
        patch.Body.Should().Contain("\"display_name\":\"renamed imap\"")
            .And.Contain("\"password\":\"\"",
                "a blank password field PATCHes an empty member — the server keeps the stored one");
        cut.FindAll("#source-error").Should().BeEmpty();
    }

    /// <summary>
    /// M2T6 review fix: the source vanishes between the list and the dialog
    /// (deleted in another tab) — the banner explains why and the actions are
    /// dead, instead of a form whose Save can only 404.
    /// </summary>
    [Fact]
    public void EditSourceDialog_Prefill404_ShowsGoneBanner_AndDisablesActions()
    {
        using var ctx = new SpaTestContext();
        ctx.Handler.Responder = (request, _) =>
            request.RequestUri!.AbsolutePath == $"/v1/user/sources/{SourceId}"
                ? Task.FromResult(SpaTestContext.Json(
                    """
                    {"type":"/errors/source-not-found","title":"Not Found","status":404,"detail":"The source does not exist."}
                    """,
                    HttpStatusCode.NotFound))
                : Task.FromResult(SpaTestContext.Json("{}"));

        var cut = ctx.RenderComponent<EditSourceDialog>(parameters => parameters
            .Add(dialog => dialog.SourceId, SourceId)
            .Add(dialog => dialog.OnCancel, EventCallback.Factory.Create(ctx, static () => { }))
            .Add(dialog => dialog.OnSaved, EventCallback.Factory.Create<SourceDto>(ctx, _ => { })));

        cut.WaitForAssertion(() =>
        {
            var error = cut.Find("#source-error");
            error.GetAttribute("role").Should().Be("alert");
            error.TextContent.Should().Contain("no longer exists");
        });

        cut.Find("#source-save").HasAttribute("disabled").Should().BeTrue(
            "Save would PATCH an id that no longer exists");
        cut.Find("#source-test").HasAttribute("disabled").Should().BeTrue(
            "Test would dial a config nobody owns any more");
        cut.Find("#source-cancel").HasAttribute("disabled").Should().BeFalse(
            "the way out stays open");

        ctx.Handler.Requests.Should().ContainSingle(
            r => r.Path == $"/v1/user/sources/{SourceId}" && r.Method == "GET",
            "nothing but the prefill GET happened — no doomed PATCH");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    /// <summary>Fills every field the schema requires (create-mode defaults elsewhere).</summary>
    private static void FillValidForm<TComponent>(
        IRenderedComponent<TComponent> cut,
        string displayName = "Greenmail inbox",
        string password = CandidatePassword)
        where TComponent : IComponent
    {
        cut.Find("#source-display-name").Change(displayName);
        cut.Find("#source-host").Change("imap.example.test");
        cut.Find("#source-username").Change("alice");
        cut.Find("#source-password").Change(password);
    }

    /// <summary>An RFC 7807 400 naming the <c>port</c> field, exactly as ConfigErrorMapper writes it.</summary>
    private static HttpResponseMessage ConfigProblem() => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent(
            """
            {"type":"/errors/config-validation","title":"Config validation failed","status":400,"detail":"The connector config is invalid.","errors":[{"name":"port","reason":"must be between 1 and 65535"}]}
            """,
            Encoding.UTF8,
            "application/problem+json"),
    };
}
