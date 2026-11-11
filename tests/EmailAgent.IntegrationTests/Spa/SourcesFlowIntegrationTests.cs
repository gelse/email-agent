using System.Net;
using System.Security.Cryptography;
using Bunit;
using EmailAgent.Core.Security;
using EmailAgent.Infrastructure;
using EmailAgent.IntegrationTests.Endpoints;
using EmailAgent.IntegrationTests.Fixtures;
using EmailAgent.Spa.Auth;
using EmailAgent.Spa.Models;
using EmailAgent.Spa.Notifications;
using EmailAgent.Spa.Security;
using EmailAgent.Spa.Sources;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

// The unlock prompt lives in EmailAgent.Spa.Components and the page in
// EmailAgent.Spa.Pages.User; the page is aliased because the typed client's
// own namespace is called EmailAgent.Spa.Sources.
using SourcesPage = EmailAgent.Spa.Pages.User.Sources;

namespace EmailAgent.IntegrationTests.Spa;

// ---------------------------------------------------------------------------
// M2T6 step 9, migrated by M5T1: the /user/sources screens driven through
// real bUnit pages over the PRODUCTION transport (BearerTokenHandler ->
// CryptoSessionHandler -> recorded seam -> TestServer) against Testcontainers
// Postgres + Greenmail. The spine is now the §13.17 unlock flow: the
// passphrase stays in the browser — GET /v1/account/kdf hands over the
// non-secret material, IUnlockCrypto derives the KEK and unwraps the DEK in
// "the browser" (bUnit has no JS runtime, so the Q-O28 seam is substituted
// with the SAME primitives in .NET: Isopoh Argon2id + AES-GCM, no production
// service and no network beyond the TestServer), and POST /v1/account/unlock
// returns the opaque X-Crypto-Session token that then rides every
// /v1/user/sources call. The recorded handler keeps method/path/body/status
// plus both headers, so the suite proves both halves of that claim (no
// session header before the unlock, the minted token after it) and that the
// passphrase NEVER appears on the wire.
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
public class SourcesFlowIntegrationTests(PostgresFixture postgres, GreenMailFixture greenmail)
    : IClassFixture<GreenMailFixture>
{
    /// <summary>The passphrase the REAL key material of the unlock facts is wrapped under.</summary>
    private const string FlowPassphrase = "m2t6-flow-passphr4se";

    private const string WrongPassphrase = "definitely-not-the-passphrase";

    /// <summary>Argon2id derivation + container-backed IMAP round-trips need headroom.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>Same-origin dev origin (nginx serves SPA + API — §13.9).</summary>
    private static readonly Uri BaseAddress = new("http://localhost:8081/");

    // -----------------------------------------------------------------------
    // The unlock flow: prompt -> real kdf + unlock -> table, headers in between.
    // -----------------------------------------------------------------------

    /// <summary>
    /// An empty crypto session hides the table behind the prompt, the WRONG
    /// passphrase fails the in-browser GCM tag check (inline error, nothing
    /// reaches the unlock endpoint), and the right one walks the real
    /// <c>GET /v1/account/kdf</c> + <c>POST /v1/account/unlock</c> pair — whose
    /// opaque token then rides every <c>/v1/user/sources</c> call as
    /// <c>X-Crypto-Session</c>. Neither the passphrase nor a DEK header ever
    /// appears on the wire, and the legacy <c>/v1/user/account/unwrap-dek</c>
    /// route is never touched.
    /// </summary>
    [Fact]
    public async Task SourcesFlow_UnlockPrompt_ThenTable_ThroughRealUnlockFlow()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        var userId = await AdminTestHost.SeedUserAsync(
            factory, SourcesTestHost.UniqueEmail("m2t6-unlock"));
        var bearer = await AdminTestHost.BearerAsync(factory, userId, "User");
        var expectedDek = await SourcesTestHost.SetRealKdfMaterialAsync(
            factory, userId, FlowPassphrase);

        using var recorder = new RecordingHandler { InnerHandler = factory.Server.CreateHandler() };
        using var flow = BuildFlow(factory, recorder, bearer, session: null, userId);

        // ---- locked session: prompt only, no table, no list request ----
        var page = flow.RenderComponent<SourcesPage>();
        page.WaitForAssertion(
            () => page.Find("#unlock-submit").Should().NotBeNull(
                "an empty crypto session presents the unlock prompt"), Wait);
        page.Markup.Should().Contain("Unlock encrypted data");
        page.FindAll("#sources-table").Should().BeEmpty();
        page.FindAll("#sources-empty").Should().BeEmpty(
            "the empty state belongs to an unlocked session, not a locked one");
        recorder.Calls.Should().BeEmpty(
            "the page must not ask the API for anything before an unlock attempt runs");

        // ---- wrong passphrase: in-browser GCM tag failure, no token, field cleared ----
        page.Find("#unlock-passphrase").Change(WrongPassphrase);
        page.Find("#unlock-submit").Click();
        page.WaitForAssertion(() =>
        {
            var error = page.Find("#unlock-error");
            error.GetAttribute("role").Should().Be("alert");
            error.TextContent.Should().Be("The passphrase is not valid.");
        }, Wait);
        flow.Store.CryptoSession.IsEmpty.Should().BeTrue(
            "a failed unwrap must never mint a session token");
        page.Find("#unlock-passphrase").GetAttribute("value").Should().BeNullOrEmpty(
            "a typo is retyped, not resubmitted blindly");

        // ---- right passphrase: real kdf + unlock, then the table ----
        page.Find("#unlock-passphrase").Change(FlowPassphrase);
        page.Find("#unlock-submit").Click();
        page.WaitForAssertion(
            () => page.Find("#sources-empty").Should().NotBeNull(
                "the unlocked page loads GET /v1/user/sources (this caller owns none)"), Wait);
        flow.Store.CryptoSession.IsEmpty.Should().BeFalse(
            "the unlock response's token is cached in the in-memory store");
        flow.Store.CryptoSession.Token.Should().NotBeNullOrWhiteSpace();
        page.FindAll("#unlock-submit").Should().BeEmpty(
            "the prompt leaves the markup once the session is unlocked");

        // ---- what actually went over the wire ----
        var kdfCalls = recorder.Calls
            .Where(call => call.Path == "/v1/account/kdf")
            .ToList();
        kdfCalls.Should().HaveCount(2, "one material fetch per attempt (the browser cannot cache)");
        kdfCalls.Should().OnlyContain(
            call => call.Status == HttpStatusCode.OK,
            "the material is non-secret and the caller is authenticated");
        kdfCalls.Should().OnlyContain(
            call => call.Authorization == $"Bearer {bearer}",
            "the kdf read is an authenticated call, like every other one here");
        kdfCalls.Should().OnlyContain(
            call => call.SessionHeader == null,
            "the account namespace carries no session header — unlock is what mints it");

        var unlocks = recorder.Calls
            .Where(call => call.Path == "/v1/account/unlock")
            .ToList();
        unlocks.Should().ContainSingle(
            "only the successful attempt POSTs a DEK — the wrong passphrase never leaves the browser");
        unlocks[0].Status.Should().Be(HttpStatusCode.OK);
        unlocks[0].Authorization.Should().Be($"Bearer {bearer}");
        unlocks[0].SessionHeader.Should().BeNull();
        unlocks[0].Body.Should().Contain(
            $"\"dek\":\"{expectedDek}\"",
            "the unwrapped DEK the stored material hides is exactly what the server wraps");
        unlocks[0].Body.Should().NotContain(FlowPassphrase,
            "the passphrase is never POSTed anywhere (Q-E9 removal)");

        recorder.Calls.Should().NotContain(
            call => call.Body != null
                && (call.Body.Contains(FlowPassphrase, StringComparison.Ordinal)
                    || call.Body.Contains(WrongPassphrase, StringComparison.Ordinal)),
            "no request body anywhere carries either passphrase");

        var list = recorder.Calls.Should().ContainSingle(
                call => call.Path.StartsWith("/v1/user/sources", StringComparison.Ordinal))
            .Which;
        list.Status.Should().Be(HttpStatusCode.OK,
            "the list decrypts each config, so 200 proves the presented X-Crypto-Session was accepted");
        list.Authorization.Should().Be($"Bearer {bearer}");
        list.SessionHeader.Should().Be(flow.Store.CryptoSession.Token,
            "CryptoSessionHandler supplies the minted token on every /v1/user/* call");

        recorder.Calls.Should().NotContain(
            call => call.Path == "/v1/user/account/unwrap-dek",
            "M5T1: the legacy unwrap route is not part of this flow at all");
    }

    // -----------------------------------------------------------------------
    // Add -> test (dialog and row) -> fetch against the live Greenmail, and
    // the delete the API refuses while history exists.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Task step 9's happy path against the real fixture: unlock through the
    /// prompt first (real kdf + unlock pair), add through the
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
        await greenmail.PurgeMailAsync();
        await greenmail.SendAsync("m2t6 flow message 1", "first body");
        await greenmail.SendAsync("m2t6 flow message 2", "second body");

        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        var userId = await AdminTestHost.SeedUserAsync(
            factory, SourcesTestHost.UniqueEmail("m2t6-flow"));
        var bearer = await AdminTestHost.BearerAsync(factory, userId, "User");
        await SourcesTestHost.SetRealKdfMaterialAsync(factory, userId, FlowPassphrase);

        using var recorder = new RecordingHandler { InnerHandler = factory.Server.CreateHandler() };
        using var flow = BuildFlow(factory, recorder, bearer, session: null, userId);

        var page = flow.RenderComponent<SourcesPage>();
        var toasts = flow.RenderComponent<ToastContainer>();

        // ---- unlock (the §13.17 flow) ----
        page.WaitForAssertion(
            () => page.Find("#unlock-submit").Should().NotBeNull(), Wait);
        page.Find("#unlock-passphrase").Change(FlowPassphrase);
        page.Find("#unlock-submit").Click();
        page.WaitForAssertion(
            () => page.Find("#sources-empty").Should().NotBeNull(), Wait);
        flow.Store.CryptoSession.IsEmpty.Should().BeFalse(
            "the unlock response's token is what the rest of this fact rides");

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
        page.Find("#source-host").Change(greenmail.ImapHost);
        page.Find("#source-port").Change(greenmail.ImapPort.ToString());
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

        // ---- transport: every sources call carries the JWT and the session token ----
        recorder.Calls.Count(
                call => call.Path == "/v1/account/kdf")
            .Should().Be(1, "one unlock attempt fetches the kdf material once");
        recorder.Calls.Count(
                call => call.Path == "/v1/account/unlock")
            .Should().Be(1, "one unlock unlocks the page");
        var sourcesCalls = recorder.Calls
            .Where(call => call.Path.StartsWith("/v1/user/sources", StringComparison.Ordinal))
            .ToList();
        sourcesCalls.Should().NotBeEmpty();
        sourcesCalls.Should().OnlyContain(
            call => call.Authorization == $"Bearer {bearer}",
            "BearerTokenHandler stamps the JWT on every call");
        sourcesCalls.Should().OnlyContain(
            call => call.SessionHeader == flow.Store.CryptoSession.Token,
            "CryptoSessionHandler supplies X-Crypto-Session on every sources call once unlocked");
        recorder.Calls.Should().NotContain(
            call => call.Body != null && call.Body.Contains(FlowPassphrase, StringComparison.Ordinal),
            "the passphrase never rides any request");
    }

    // -----------------------------------------------------------------------
    // The confirming delete: row removal, empty state, no orphaned blob.
    // -----------------------------------------------------------------------

    /// <summary>
    /// The delete step 9 ends on: confirm modal -> DELETE -> the row is gone,
    /// the empty state returns and neither the source row nor its encrypted
    /// config blob survives. This session starts UNLOCKED (the prompt itself is
    /// proven by the two facts above) with a crypto session minted straight
    /// through POST /v1/account/unlock before the first render.
    /// </summary>
    [Fact]
    public async Task SourcesFlow_Delete_RemovesRow_AndShowsEmptyState()
    {
        var connectionString = await AccountTestHost.MigrateAsync(postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        var userId = await AdminTestHost.SeedUserAsync(
            factory, SourcesTestHost.UniqueEmail("m2t6-delete"));
        var bearer = await AdminTestHost.BearerAsync(factory, userId, "User");
        var session = await SourcesTestHost.UnlockAsync(
            AccountTestHost.CreateClient(factory), bearer, SourcesTestHost.NewDek());

        using var recorder = new RecordingHandler { InnerHandler = factory.Server.CreateHandler() };
        using var flow = BuildFlow(factory, recorder, bearer, session, userId);

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
        recorder.Calls.Should().OnlyContain(
            call => call.SessionHeader == session,
            "the pre-minted token rides every sources call this fact makes");
    }

    // -----------------------------------------------------------------------
    // Rig: the production handler chain over the TestServer plus the bUnit
    // context that registers what Program.cs registers.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Bearer outside, crypto-session inside — the order Program.cs registers
    /// them, so a 401-retry clone passes back through CryptoSessionHandler
    /// instead of losing the session header. The caller's token (and optional
    /// pre-minted crypto session) is in the store before the first component
    /// renders.
    /// </summary>
    private static SourcesFlowContext BuildFlow(
        WebApplicationFactory<Program> factory,
        RecordingHandler recorder,
        string bearer,
        string? session,
        Guid userId)
    {
        var store = new TokenStore();
        store.Set(bearer);
        if (session is not null)
        {
            store.CryptoSession.SetToken(session);
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
                InnerHandler = new CryptoSessionHandler(store) { InnerHandler = recorder },
            })
        {
            BaseAddress = BaseAddress,
        };

        var encryption = factory.Services.GetRequiredService<IEncryptionService>();
        return new SourcesFlowContext(apiHttp, store, encryption, userId);
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
    /// The Q-O28 seam in-process: the SAME primitives the browser build uses
    /// (Isopoh Argon2id, as <c>Argon2idAesGcmEncryptionService.DeriveKekCore</c>
    /// does, plus AES-256-GCM over ciphertext||tag without AAD, as
    /// <c>GenerateAndWrapDek</c> wraps it) standing in for the vendored
    /// argon2-browser WASM + crypto.subtle that bUnit cannot run. A wrong
    /// passphrase fails the GCM tag here exactly like it does in the browser,
    /// so <see cref="WrongPassphraseException"/> reaches the prompt unchanged.
    /// No production service and no socket is involved (AGENTS.md hard
    /// constraint 3): <see cref="IEncryptionService"/> is the library the test
    /// host already resolved.
    /// </summary>
    private sealed class DotNetUnlockCrypto(IEncryptionService encryption, Guid userId) : IUnlockCrypto
    {
        private const int NonceByteLength = 12;
        private const int GcmTagByteLength = 16;

        public Task<byte[]> DeriveKekAsync(
            string passphrase,
            byte[] salt,
            KdfParamsDto kdfParams,
            CancellationToken ct = default)
            => Task.FromResult(encryption.DeriveKek(userId, passphrase, salt, new KdfParams
            {
                MemoryKb = kdfParams.MemoryKb,
                Iterations = kdfParams.Iterations,
                Parallelism = kdfParams.Parallelism,
                HashLength = kdfParams.HashLength,
            }));

        public Task<byte[]> UnwrapDekAsync(
            ReadOnlyMemory<byte> kek,
            byte[] dekWrapped,
            byte[] dekNonce,
            CancellationToken ct = default)
        {
            if (dekNonce.Length != NonceByteLength || dekWrapped.Length <= GcmTagByteLength)
            {
                throw new UnlockCryptoException("Malformed DEK wrap.");
            }

            try
            {
                var plaintext = new byte[dekWrapped.Length - GcmTagByteLength];
                using var aes = new AesGcm(kek.Span, GcmTagByteLength);
                aes.Decrypt(
                    dekNonce,
                    dekWrapped.AsSpan(0, plaintext.Length),
                    dekWrapped.AsSpan(plaintext.Length),
                    plaintext);
                return Task.FromResult(plaintext);
            }
            catch (CryptographicException ex)
            {
                // The GCM tag did not authenticate — the wrong-passphrase
                // verdict the browser's crypto.subtle produces.
                throw new WrongPassphraseException("The passphrase is not valid.", ex);
            }
        }
    }

    /// <summary>
    /// bUnit context for the flow: the production HttpClient (bearer handler +
    /// crypto-session handler over the TestServer), the singleton token store
    /// the chain and the pages share, and the Program.cs registrations the
    /// unlock prompt and sources page need (account client + unlock service
    /// over the same HttpClient, with the Q-O28 crypto seam substituted by
    /// <see cref="DotNetUnlockCrypto"/> — bUnit has no JS runtime).
    /// </summary>
    private sealed class SourcesFlowContext : TestContext
    {
        public SourcesFlowContext(HttpClient api, TokenStore store, IEncryptionService encryption, Guid userId)
        {
            Services.AddSingleton(store);
            Services.AddSingleton(api);
            Services.AddSingleton<JwtRoleParser>();
            Services.AddSingleton<ToastService>();
            Services.AddScoped(sp => new SourcesClient(sp.GetRequiredService<HttpClient>()));
            Services.AddScoped(sp => new AccountClient(sp.GetRequiredService<HttpClient>()));
            Services.AddScoped<IUnlockCrypto>(
                _ => new DotNetUnlockCrypto(encryption, userId));
            Services.AddScoped<UnlockService>();
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
    /// <c>Authorization</c> and the session-key envelope <c>X-Crypto-Session</c>
    /// — captured after both handlers stamped the request and before the
    /// server saw it.
    /// </summary>
    private sealed class RecordingHandler : DelegatingHandler
    {
        public sealed record RecordedCall(
            string Method,
            string Path,
            string? Body,
            HttpStatusCode Status,
            string? Authorization,
            string? SessionHeader);

        private readonly List<RecordedCall> _calls = [];

        public IReadOnlyList<RecordedCall> Calls
        {
            get
            {
                lock (_calls)
                {
                    return [.. _calls];
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
            var sessionHeader = request.Headers.TryGetValues(
                    CryptoSessionHandler.HeaderName, out var values)
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
                    sessionHeader));
            }

            return response;
        }
    }
}
