using System.Net;
using Bunit;
using EmailAgent.Infrastructure;
using EmailAgent.IntegrationTests.Endpoints;
using EmailAgent.IntegrationTests.Fixtures;
using EmailAgent.Spa.Auth;
using EmailAgent.Spa.Notifications;
using EmailAgent.Spa.Sources;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

// The unlock page lives in EmailAgent.Spa.Pages.User; it is aliased because
// the typed client's own namespace is called EmailAgent.Spa.Sources.
using SourcesPage = EmailAgent.Spa.Pages.User.Sources;

namespace EmailAgent.IntegrationTests.Spa;

// ---------------------------------------------------------------------------
// M2T6 step 9: the /user/sources screens driven through real bUnit pages over
// the PRODUCTION transport (BearerTokenHandler -> DekHeaderHandler -> recorded
// seam -> TestServer) against Testcontainers Postgres + Greenmail. Adjudication
// R2 is the spine: the passphrase is POSTed to /v1/user/account/unwrap-dek, the
// server unwraps the caller's own key, and only afterwards does X-User-Dek ride
// every sources call — the recorded handler keeps method/path/body/status plus
// both auth headers, so the suite proves both halves of that claim (no key
// header before the unlock, the server's own key after it) and that
// GET /v1/user/account/kdf is never called at all (nothing derives a key in the
// browser).
//
// Delete is split across the last two facts on purpose: a source that already
// has processed_messages rows cannot be removed in M2 (Q-O15 -> 409
// /errors/conflict; the erasure cascade lands with M6aT7), so the fact that
// fetches real mail proves that refusal as it surfaces in the modal, and a
// separate fact proves the confirming delete the DoD asks for — row gone,
// empty state back — on a source without history. As in M2T5's harness the
// token is minted by the real issuer rather than walked through the M1 login
// flow (the login flow has its own suite in AuthFlowIntegrationTests, and this
// keeps the per-user rate-limit budget for the sources calls).
// ---------------------------------------------------------------------------

[Collection("Postgres")]
public class SourcesFlowIntegrationTests : IClassFixture<GreenMailFixture>
{
    /// <summary>The passphrase the REAL key material of the unlock facts is wrapped under.</summary>
    private const string FlowPassphrase = "m2t6-flow-passphr4se";

    private const string WrongPassphrase = "definitely-not-the-passphrase";

    /// <summary>Argon2id unwrap + container-backed IMAP round-trips need headroom.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>Same-origin dev origin (nginx serves SPA + API — §13.9).</summary>
    private static readonly Uri BaseAddress = new("http://localhost:8081/");

    private readonly PostgresFixture _postgres;
    private readonly GreenMailFixture _greenmail;

    public SourcesFlowIntegrationTests(PostgresFixture postgres, GreenMailFixture greenmail)
    {
        _postgres = postgres;
        _greenmail = greenmail;
    }

    // -----------------------------------------------------------------------
    // R2: unlock prompt -> real unwrap -> table, header semantics in between.
    // -----------------------------------------------------------------------

    /// <summary>
    /// An empty <c>DekStore</c> hides the table behind the prompt, the WRONG
    /// passphrase is a 401 the SPA renders inline (nothing reaches the store),
    /// and the right one hands back the server's own DEK — which then rides
    /// every <c>/v1/user/sources</c> call. The unwrap POSTs carry no
    /// <c>X-User-Dek</c> (the key does not exist yet) and no
    /// <c>GET /v1/user/account/kdf</c> happens at all.
    /// </summary>
    [Fact]
    public async Task SourcesFlow_UnlockPrompt_ThenTable_ThroughRealUnwrapDek()
    {
        var connectionString = await AccountTestHost.MigrateAsync(_postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        var userId = await AdminTestHost.SeedUserAsync(
            factory, SourcesTestHost.UniqueEmail("m2t6-unlock"));
        var bearer = await AdminTestHost.BearerAsync(factory, userId, "User");
        var expectedDek = await SourcesTestHost.SetRealKdfMaterialAsync(
            factory, userId, FlowPassphrase);

        using var recorder = new RecordingHandler { InnerHandler = factory.Server.CreateHandler() };
        using var flow = BuildFlow(factory, recorder, bearer, dek: null);

        // ---- locked session: prompt only, no table, no list request ----
        var page = flow.RenderComponent<SourcesPage>();
        page.WaitForAssertion(
            () => page.Find("#unlock-submit").Should().NotBeNull(
                "an empty DekStore presents the R2 passphrase prompt"), Wait);
        page.Markup.Should().Contain("Unlock encrypted sources");
        page.FindAll("#sources-table").Should().BeEmpty();
        page.FindAll("#sources-empty").Should().BeEmpty(
            "the empty state belongs to an unlocked session, not a locked one");
        recorder.Calls.Should().BeEmpty(
            "the page must not ask the API for anything before it holds a key");

        // ---- wrong passphrase: inline 401, no key cached, field cleared ----
        page.Find("#unlock-passphrase").Change(WrongPassphrase);
        page.Find("#unlock-submit").Click();
        page.WaitForAssertion(() =>
        {
            var error = page.Find("#unlock-error");
            error.GetAttribute("role").Should().Be("alert");
            error.TextContent.Should().Be("The passphrase is not valid.");
        }, Wait);
        flow.Store.DekStore.IsEmpty.Should().BeTrue(
            "a failed unwrap must never cache a key");
        page.Find("#unlock-passphrase").GetAttribute("value").Should().BeNullOrEmpty(
            "a typo is retyped, not resubmitted blindly");

        // ---- right passphrase: the server's own key, then the table ----
        page.Find("#unlock-passphrase").Change(FlowPassphrase);
        page.Find("#unlock-submit").Click();
        page.WaitForAssertion(
            () => page.Find("#sources-empty").Should().NotBeNull(
                "the unlocked page loads GET /v1/user/sources (this caller owns none)"), Wait);
        flow.Store.DekStore.DekBase64.Should().Be(expectedDek,
            "the cached key is exactly the one the server unwrapped for this caller");
        page.FindAll("#unlock-submit").Should().BeEmpty(
            "the prompt leaves the markup once the session is unlocked");

        // ---- what actually went over the wire ----
        var unwraps = recorder.Calls
            .Where(call => call.Path == "/v1/user/account/unwrap-dek")
            .ToList();
        unwraps.Should().HaveCount(2, "one unwrap attempt per passphrase");
        unwraps[0].Status.Should().Be(HttpStatusCode.Unauthorized);
        unwraps[1].Status.Should().Be(HttpStatusCode.OK);
        unwraps.Should().OnlyContain(
            call => call.DekHeader == null,
            "X-User-Dek cannot ride the unwrap — the key does not exist until it returns");
        unwraps.Should().OnlyContain(
            call => call.Authorization == $"Bearer {bearer}",
            "the unwrap is an authenticated call, like every other one here");
        unwraps[0].Body.Should().Contain(WrongPassphrase);
        unwraps[1].Body.Should().Contain(
            $"\"passphrase\":\"{FlowPassphrase}\"",
            "the passphrase travels exactly once, in the request body");

        var list = recorder.Calls.Should().ContainSingle(
                call => call.Path.StartsWith("/v1/user/sources", StringComparison.Ordinal))
            .Which;
        list.Status.Should().Be(HttpStatusCode.OK,
            "the list decrypts each config, so 200 proves the returned key was accepted as X-User-Dek");
        list.Authorization.Should().Be($"Bearer {bearer}");
        list.DekHeader.Should().Be(expectedDek);

        recorder.Calls.Should().NotContain(
            call => call.Path.StartsWith("/v1/user/account/kdf", StringComparison.Ordinal),
            "R2: no GET /kdf either — no Argon2 material is ever fetched for in-browser derivation");
    }

    // -----------------------------------------------------------------------
    // Add -> test (dialog and row) -> fetch against the live Greenmail, and
    // the delete the API refuses while history exists.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Task step 9's happy path against the real fixture: add through the
    /// dialog (schema-mirroring client check, then a REAL Greenmail connection
    /// test before save), the row's inline password panel with a wrong and a
    /// right password, fetch-now's info toast with the counts plus the
    /// persisted rows — and finally the DELETE the API answers 409 for while
    /// the source has processed messages (Q-O15), surfaced in the modal
    /// instead of a silent no-op. The confirming delete lives in
    /// <see cref="SourcesFlow_Delete_RemovesRow_AndShowsEmptyState"/>.
    /// </summary>
    [Fact]
    public async Task SourcesFlow_Add_Test_Fetch_ThenConflictDelete_ThroughRealApi()
    {
        await _greenmail.PurgeMailAsync();
        await _greenmail.SendAsync("m2t6 flow message 1", "first body");
        await _greenmail.SendAsync("m2t6 flow message 2", "second body");

        var connectionString = await AccountTestHost.MigrateAsync(_postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        var userId = await AdminTestHost.SeedUserAsync(
            factory, SourcesTestHost.UniqueEmail("m2t6-flow"));
        var bearer = await AdminTestHost.BearerAsync(factory, userId, "User");
        var dek = await SourcesTestHost.SetRealKdfMaterialAsync(factory, userId, FlowPassphrase);

        using var recorder = new RecordingHandler { InnerHandler = factory.Server.CreateHandler() };
        using var flow = BuildFlow(factory, recorder, bearer, dek: null);

        var page = flow.RenderComponent<SourcesPage>();
        var toasts = flow.RenderComponent<ToastContainer>();

        // ---- unlock (R2) ----
        page.WaitForAssertion(
            () => page.Find("#unlock-submit").Should().NotBeNull(), Wait);
        page.Find("#unlock-passphrase").Change(FlowPassphrase);
        page.Find("#unlock-submit").Click();
        page.WaitForAssertion(
            () => page.Find("#sources-empty").Should().NotBeNull(), Wait);
        flow.Store.DekStore.DekBase64.Should().Be(dek);

        // ---- add through the dialog, tested against the live Greenmail ----
        page.Find("#add-source-open").Click();

        // Round 5: the dialog's fields are looked up immediately after the
        // click; under full-suite load the render has occasionally not
        // propagated yet (observed once as ElementNotFoundException), so wait
        // for the form the way the rest of this file does.
        page.WaitForAssertion(
            () => page.Find("#source-display-name").Should().NotBeNull(
                "the add dialog renders its form as soon as it is opened"), Wait);
        page.Find("#source-display-name").Change("greenmail inbox");
        page.Find("#source-host").Change(_greenmail.ImapHost);
        page.Find("#source-port").Change(_greenmail.ImapPort.ToString());
        page.Find("#source-use-ssl").Change(false); // 3143 speaks plaintext IMAP
        page.Find("#source-username").Change(GreenMailFixture.Username);
        page.Find("#source-password").Change(GreenMailFixture.Password);
        page.Find("#source-mailbox").Change("INBOX");

        page.Find("#source-test").Click();
        toasts.WaitForAssertion(() => toasts.Markup.Should().Contain(
            "Connection to 'greenmail inbox' succeeded.",
            "valid credentials must surface as a success toast (acceptance criterion)"), Wait);
        flow.Toasts.Items.Should().ContainSingle(t => t.Kind == ToastKind.Success);
        page.FindAll("#source-test-error").Should().BeEmpty();

        page.Find("#source-save").Click();
        page.WaitForAssertion(
            () => page.FindAll("#source-display-name").Should().BeEmpty(
                "the parent closes the dialog on OnSaved"), Wait);
        var sourceId = await SingleSourceIdAsync(factory, userId);
        page.WaitForAssertion(
            () => page.FindAll($"#source-delete-{sourceId:N}").Should().ContainSingle(
                "the post-save reload renders the created row"), Wait);

        var create = recorder.Calls.Should().ContainSingle(
                call => call.Method == "POST" && call.Path == "/v1/user/sources")
            .Which;
        create.Body.Should().Contain("\"display_name\":\"greenmail inbox\"")
            .And.Contain("\"use_ssl\":false")
            .And.Contain($"\"password\":\"{GreenMailFixture.Password}\"",
                "the create carries the full candidate config (the API stores it encrypted)");

        // ---- row panel: wrong password inline, right password success toast ----
        page.Find($"#source-test-{sourceId:N}").Click();
        page.Find("#row-test-password").Change("definitely-not-the-password");
        page.Find("#row-test-submit").Click();
        page.WaitForAssertion(() =>
        {
            var error = page.Find("#row-test-error");
            error.GetAttribute("role").Should().Be("alert");
            error.TextContent.Should().Contain("authentication failed",
                "Greenmail genuinely rejects the wrong LOGIN — this is the live server");
        }, Wait);
        page.Find("#row-test-password").Should().NotBeNull(
            "the panel stays open so the password can be retyped");

        ClearToasts(flow);
        page.Find("#row-test-password").Change(GreenMailFixture.Password);
        page.Find("#row-test-submit").Click();
        page.WaitForAssertion(
            () => page.FindAll("#row-test-password").Should().BeEmpty(
                "the panel closes only on { ok: true }"), Wait);
        toasts.WaitForAssertion(() => flow.Toasts.Items.Should().ContainSingle(
                t => t.Kind == ToastKind.Success
                    && t.Message == "Connection to 'greenmail inbox' succeeded."),
            Wait);

        // ---- fetch now: the info toast carries the counts, the rows persist ----
        page.Find($"#source-fetch-{sourceId:N}").Click();
        toasts.WaitForAssertion(() => flow.Toasts.Items.Should().ContainSingle(
                t => t.Kind == ToastKind.Info
                    && t.Message.Contains("2 new message(s)", StringComparison.Ordinal),
                "the acceptance criterion pins an INFO toast with the count of new messages"),
            Wait);
        page.WaitForAssertion(() =>
        {
            var cells = page.FindAll("tbody tr td").Select(td => td.TextContent).ToList();
            cells.Should().HaveCount(7, "one row, seven columns");
            cells[4].Should().NotBe("—", "fetch-now stamps last_run_at on the source");
        }, Wait);
        (await SourcesTestHost.MessagesAsync(factory, sourceId)).Should().HaveCount(
            2, "both seeded messages are persisted, dedupe-keyed on this source");

        // ---- DELETE while history exists: 409 rendered inside the modal ----
        page.Find($"#source-delete-{sourceId:N}").Click();
        page.Find("#delete-source-title").TextContent.Should().Be("Delete source");
        page.Find("#delete-source-confirm").Click();
        page.WaitForAssertion(() =>
        {
            var error = page.Find("#delete-source-error");
            error.GetAttribute("role").Should().Be("alert");
            error.TextContent.Should().Contain("still has processed messages",
                "the API's Q-O15 refusal arrives as RFC 7807, not as a silent no-op");
        }, Wait);
        page.Find("#delete-source-cancel").Click();
        page.WaitForAssertion(
            () => page.FindAll("#delete-source-title").Should().BeEmpty(), Wait);
        page.FindAll($"#source-delete-{sourceId:N}").Should().NotBeEmpty(
            "Q-O15 keeps the source and its message rows until M6aT7's erasure cascade");
        (await SourcesTestHost.MessagesAsync(factory, sourceId)).Should().HaveCount(2);
        (await SourceCountAsync(factory, userId)).Should().Be(1);

        // ---- transport: every sources call carries the JWT and the M2-only key ----
        recorder.Calls.Count(
                call => call.Path == "/v1/user/account/unwrap-dek")
            .Should().Be(1, "one unlock unlocks the page");
        var sourcesCalls = recorder.Calls
            .Where(call => call.Path.StartsWith("/v1/user/sources", StringComparison.Ordinal))
            .ToList();
        sourcesCalls.Should().NotBeEmpty();
        sourcesCalls.Should().OnlyContain(
            call => call.Authorization == $"Bearer {bearer}",
            "BearerTokenHandler stamps the JWT on every call");
        sourcesCalls.Should().OnlyContain(
            call => call.DekHeader == dek,
            "DekHeaderHandler supplies X-User-Dek on every sources call once unlocked");
    }

    // -----------------------------------------------------------------------
    // The confirming delete: row removal, empty state, no orphaned blob.
    // -----------------------------------------------------------------------

    /// <summary>
    /// The delete step 9 ends on: confirm modal -> DELETE -> the row is gone,
    /// the empty state returns and neither the source row nor its encrypted
    /// config blob survives. This session starts UNLOCKED (the prompt itself is
    /// proven by the two facts above) with a fresh DEK the dialog's POST
    /// encrypts under.
    /// </summary>
    [Fact]
    public async Task SourcesFlow_Delete_RemovesRow_AndShowsEmptyState()
    {
        var connectionString = await AccountTestHost.MigrateAsync(_postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        var userId = await AdminTestHost.SeedUserAsync(
            factory, SourcesTestHost.UniqueEmail("m2t6-delete"));
        var bearer = await AdminTestHost.BearerAsync(factory, userId, "User");
        var dek = SourcesTestHost.NewDek();

        using var recorder = new RecordingHandler { InnerHandler = factory.Server.CreateHandler() };
        using var flow = BuildFlow(factory, recorder, bearer, dek);

        var page = flow.RenderComponent<SourcesPage>();
        var toasts = flow.RenderComponent<ToastContainer>();
        page.WaitForAssertion(
            () => page.Find("#sources-empty").Should().NotBeNull(
                "an unlocked session loads the list immediately — no prompt"), Wait);
        page.FindAll("#unlock-submit").Should().BeEmpty();

        // ---- add (client-side required check runs before the POST) ----
        page.Find("#add-source-open").Click();
        page.WaitForAssertion(
            () => page.Find("#source-display-name").Should().NotBeNull(
                "the add dialog renders its form as soon as it is opened"), Wait);
        page.Find("#source-display-name").Change("to be deleted");
        page.Find("#source-host").Change("imap.example.test");
        page.Find("#source-username").Change("alice");
        page.Find("#source-password").Change("s3cret-pass");
        page.Find("#source-save").Click();

        page.WaitForAssertion(
            () => page.FindAll("#source-display-name").Should().BeEmpty(
                "the parent closes the dialog on OnSaved — the POST has answered 201"), Wait);
        var sourceId = await SingleSourceIdAsync(factory, userId);
        page.WaitForAssertion(
            () => page.FindAll($"#source-delete-{sourceId:N}").Should().ContainSingle(), Wait);
        var blobId = (await SourcesTestHost.SourceRowAsync(factory, sourceId)).ConfigBlobId;

        // ---- confirm -> row removal + empty state ----
        page.Find($"#source-delete-{sourceId:N}").Click();
        page.Find("#delete-source-title").TextContent.Should().Be("Delete source");
        page.Find("#delete-source-confirm").Click();
        page.WaitForAssertion(() =>
        {
            page.FindAll("#delete-source-title").Should().BeEmpty(
                "the modal closes once the API answers 204");
            page.Find("#sources-empty").Should().NotBeNull(
                "the reload after the delete finds no rows");
        }, Wait);
        toasts.WaitForAssertion(() => flow.Toasts.Items.Should().ContainSingle(
                t => t.Kind == ToastKind.Success
                    && t.Message == "Source 'to be deleted' deleted."), Wait);

        (await SourceCountAsync(factory, userId)).Should().Be(
            0, "the message_sources row is gone");
        (await SourcesTestHost.FindBlobAsync(factory, blobId)).Should().BeNull(
            "SQ-7: the config blob is removed together with its source");
    }

    // -----------------------------------------------------------------------
    // Rig: the production handler chain over the TestServer plus the bUnit
    // context that registers what Program.cs registers.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Bearer outside, DEK inside — the order Program.cs registers them, so a
    /// 401-retry clone passes back through DekHeaderHandler instead of losing
    /// the key header. The caller's token (and optionally DEK) is in the store
    /// before the first component renders.
    /// </summary>
    private static SourcesFlowContext BuildFlow(
        WebApplicationFactory<Program> factory,
        RecordingHandler recorder,
        string bearer,
        string? dek)
    {
        var store = new TokenStore();
        store.Set(bearer);
        if (dek is not null)
        {
            store.SetDek(dek);
        }

        var refreshHttp = new HttpClient(factory.Server.CreateHandler())
        {
            BaseAddress = BaseAddress,
        };
        var authenticator = new RefreshCookieAuthenticator(
            new FixedClientFactory(refreshHttp), store);

        var apiHttp = new HttpClient(
            new BearerTokenHandler(store, authenticator)
            {
                InnerHandler = new DekHeaderHandler(store) { InnerHandler = recorder },
            })
        {
            BaseAddress = BaseAddress,
        };

        return new SourcesFlowContext(apiHttp, store);
    }

    /// <summary>Removes every live toast so the next assertion can count exactly the new one.</summary>
    private static void ClearToasts(SourcesFlowContext flow)
    {
        foreach (var toast in flow.Toasts.Items)
        {
            flow.Toasts.Dismiss(toast.Id);
        }
    }

    private static async Task<Guid> SingleSourceIdAsync(
        WebApplicationFactory<Program> factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var source = await db.MessageSources.AsNoTracking()
            .SingleAsync(row => row.UserId == userId);
        return source.Id;
    }

    private static async Task<int> SourceCountAsync(
        WebApplicationFactory<Program> factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        return await db.MessageSources.AsNoTracking()
            .CountAsync(row => row.UserId == userId);
    }

    /// <summary>
    /// bUnit context for the flow: the production HttpClient (bearer handler +
    /// DEK handler over the TestServer), the singleton token store the chain
    /// and the pages share, and the M2T6 registrations from Program.cs (toast
    /// bus + typed sources client over that same HttpClient).
    /// </summary>
    private sealed class SourcesFlowContext : TestContext
    {
        public SourcesFlowContext(HttpClient api, TokenStore store)
        {
            Services.AddSingleton(store);
            Services.AddSingleton(api);
            Services.AddSingleton<JwtRoleParser>();
            Services.AddSingleton<ToastService>();
            Services.AddScoped(sp => new SourcesClient(sp.GetRequiredService<HttpClient>()));
        }

        public TokenStore Store => Services.GetRequiredService<TokenStore>();

        public ToastService Toasts => Services.GetRequiredService<ToastService>();
    }

    /// <summary>IHttpClientFactory returning the one configured client (refresh path).</summary>
    private sealed class FixedClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    /// <summary>
    /// The recording seam between the SPA's handler chain and the TestServer:
    /// method, path, body and status plus the two headers under test —
    /// <c>Authorization</c> and the M2-only <c>X-User-Dek</c> — captured after
    /// both handlers stamped the request and before the server saw it.
    /// </summary>
    private sealed class RecordingHandler : DelegatingHandler
    {
        public sealed record RecordedCall(
            string Method,
            string Path,
            string? Body,
            HttpStatusCode Status,
            string? Authorization,
            string? DekHeader);

        private readonly List<RecordedCall> _calls = new();

        public IReadOnlyList<RecordedCall> Calls
        {
            get
            {
                lock (_calls)
                {
                    return _calls.ToArray();
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Buffered once, like the browser-equivalent cookie jar of
            // AuthFlowIntegrationTests — downstream still receives the body.
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var authorization = request.Headers.Authorization?.ToString();
            var dekHeader = request.Headers.TryGetValues(
                    DekHeaderHandler.HeaderName, out var values)
                ? string.Join(",", values)
                : null;
            var path = request.RequestUri?.PathAndQuery ?? "/";

            var response = await base.SendAsync(request, cancellationToken);

            lock (_calls)
            {
                _calls.Add(new RecordedCall(
                    request.Method.Method,
                    path,
                    body,
                    response.StatusCode,
                    authorization,
                    dekHeader));
            }

            return response;
        }
    }
}
