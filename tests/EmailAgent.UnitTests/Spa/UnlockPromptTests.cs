using System.Net;
using System.Reflection;
using Bunit;
using EmailAgent.Spa.Auth;
using EmailAgent.Spa.Components;
using EmailAgent.Spa.Models;
using EmailAgent.Spa.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmailAgent.UnitTests.Spa;

/// <summary>
/// M5T1 bUnit coverage for the vault-unlock mechanism (SQ-M5-1 / §13.17):
/// the prompt posts the unwrapped DEK once to <c>POST /v1/account/unlock</c>
/// (never the passphrase), <see cref="UnlockService"/> drives the browser
/// crypto seam with the server's stored material, <see cref="CryptoSessionHandler"/>
/// attaches <c>X-Crypto-Session</c> to <c>/v1/user/*</c> only, and the token
/// has a memory-only lifetime. All over <see cref="StubHttpHandler"/> and
/// <see cref="FakeUnlockCrypto"/> — no real crypto, no real service (AGENTS.md
/// hard constraint 3). The screen-level flow belongs to M5T8.
/// </summary>
public class UnlockPromptTests
{
    private const string Passphrase = "correct horse battery staple";

    // The account's stored KDF material as GET /v1/account/kdf serves it —
    // every value passes UnlockService.Validate()'s bounds mirror.
    private static readonly byte[] Salt = Enumerable.Range(0, 16).Select(i => (byte)(0x5A + i)).ToArray();
    private static readonly byte[] DekWrapped = Enumerable.Range(0, 48).Select(i => (byte)i).ToArray();
    private static readonly byte[] DekNonce = Enumerable.Range(0, 12).Select(i => (byte)(0xC0 + i)).ToArray();

    private static string KdfBody() => $$"""
        {"kdf_salt":"{{Convert.ToBase64String(Salt)}}","kdf_params":{"memory_kb":19456,"iterations":2,"parallelism":1,"hash_length":32},"dek_wrapped":"{{Convert.ToBase64String(DekWrapped)}}","dek_nonce":"{{Convert.ToBase64String(DekNonce)}}"}
        """;

    private static string UnlockBody() =>
        $"{{\"crypto_session_id\":\"{Guid.NewGuid()}\",\"crypto_session_token\":\"{SpaTestContext.CryptoSessionToken}\"}}";

    /// <summary>Answers kdf + unlock like the API (AccountEndpoints) and lets everything else 404 loudly.</summary>
    private static void ConfigureUnlockFlow(SpaTestContext ctx)
    {
        ctx.Handler.Responder = (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(path switch
            {
                AccountClient.KdfPath => SpaTestContext.Json(KdfBody()),
                AccountClient.UnlockPath when request.Method == HttpMethod.Post
                    => SpaTestContext.Json(UnlockBody()),
                _ => SpaTestContext.Json(
                    "{\"type\":\"/errors/not-found\",\"title\":\"Not Found\",\"status\":404}",
                    HttpStatusCode.NotFound),
            });
        };
    }

    // ----------------------------------------------------------------- prompt

    [Fact]
    public void UnlockPrompt_PromptsPassphrase_AndPostsDek_OnSubmit()
    {
        using var ctx = new SpaTestContext();
        ConfigureUnlockFlow(ctx);

        var unlocked = false;
        var cut = ctx.RenderComponent<UnlockPrompt>(parameters => parameters
            .Add(prompt => prompt.OnUnlocked, EventCallback.Factory.Create(
                ctx, () => unlocked = true)));

        cut.Markup.Should().Contain("Unlock encrypted data");
        cut.Find("#unlock-passphrase").GetAttribute("type").Should().Be("password",
            "the passphrase is typed into a masked field");

        cut.Find("#unlock-passphrase").Change(Passphrase);
        cut.Find("#unlock-submit").Click();

        cut.WaitForAssertion(() => unlocked.Should().BeTrue(
            "a 200 unlock hands the session token to the page and closes the prompt"));

        // Exactly two calls: the non-secret material, then the DEK once.
        ctx.Handler.Requests.Should().HaveCount(2, "nothing else is contacted during unlock");
        ctx.Handler.Requests[0].Path.Should().Be(AccountClient.KdfPath);
        var unlock = ctx.Handler.Requests.Should().ContainSingle(
            r => r.Path == AccountClient.UnlockPath).Which;
        unlock.Method.Should().Be("POST");
        unlock.Body.Should().Contain($"\"dek\":\"{Convert.ToBase64String(FakeUnlockCrypto.Dek)}\"",
            "the browser-unwrapped DEK crosses the wire exactly once, base64");
        unlock.Body.Should().NotContain(Passphrase,
            "the passphrase is derived in the browser and must never appear on the wire");

        ctx.Store.CryptoSession.Token.Should().Be(SpaTestContext.CryptoSessionToken,
            "the returned opaque token is what X-Crypto-Session will carry");
        cut.Find("#unlock-passphrase").GetAttribute("value").Should().BeNullOrEmpty(
            "the typed passphrase is cleared once the attempt finished");
        cut.FindAll("#unlock-error").Should().BeEmpty();
    }

    // ----------------------------------------------------------------- service

    [Fact]
    public async Task UnlockService_DerivesKek_UnwrapsDek_UsesKdfMaterial()
    {
        using var ctx = new SpaTestContext();
        ConfigureUnlockFlow(ctx);
        var service = ctx.Services.GetRequiredService<UnlockService>();

        var outcome = await service.UnlockAsync(Passphrase);

        outcome.Status.Should().Be(UnlockStatus.Unlocked);
        outcome.Message.Should().BeNull("a success carries no user-facing error");

        // Step 1: the KEK derivation received the passphrase and the exact
        // material the server shipped (salt + stored Argon2id parameters).
        ctx.FakeCrypto.DerivedPassphrase.Should().Be(Passphrase);
        ctx.FakeCrypto.DerivedSalt.Should().Equal(Salt,
            "the KEK is derived over the account's kdf_salt");
        ctx.FakeCrypto.DerivedParams.Should().NotBeNull();
        ctx.FakeCrypto.DerivedParams!.MemoryKb.Should().Be(19456);
        ctx.FakeCrypto.DerivedParams.Iterations.Should().Be(2);
        ctx.FakeCrypto.DerivedParams.Parallelism.Should().Be(1);
        ctx.FakeCrypto.DerivedParams.HashLength.Should().Be(32);

        // Step 2: the unwrap consumed THAT kek and the stored wrap/nonce
        // (recorded as copies — UnlockService zeroes the real buffers).
        ctx.FakeCrypto.UnwrappedKek.Should().Equal(FakeUnlockCrypto.Kek,
            "the KEK handed to the unwrap is the one just derived");
        ctx.FakeCrypto.UnwrappedWrapped.Should().Equal(DekWrapped);
        ctx.FakeCrypto.UnwrappedNonce.Should().Equal(DekNonce);

        // Step 3: the DEK (only the DEK) was POSTed once and the token cached.
        var unlock = ctx.Handler.Requests.Should().ContainSingle(
            r => r.Path == AccountClient.UnlockPath).Which;
        unlock.Body.Should().Contain(Convert.ToBase64String(FakeUnlockCrypto.Dek));
        unlock.Body.Should().NotContain(Passphrase);
        ctx.Store.CryptoSession.Token.Should().Be(SpaTestContext.CryptoSessionToken);
    }

    // ----------------------------------------------------------------- attach handler

    [Fact]
    public async Task CryptoSessionHandler_AttachesX_CryptoSession_WhenUnlocked()
    {
        var store = new TokenStore();
        store.Set(SpaTestContext.FakeJwt(role: "User"));
        store.CryptoSession.SetToken(SpaTestContext.CryptoSessionToken);
        var recorder = new RecordingHandler();
        using var client = Client(store, recorder);

        using (await client.GetAsync("/v1/user/sources")) { }
        using (await client.GetAsync(AccountClient.KdfPath)) { }

        recorder.Seen.Should().HaveCount(2);
        recorder.Seen[0].Path.Should().StartWith("/v1/user/", "the enforced namespace");
        recorder.Seen[0].SessionHeader.Should().Be(SpaTestContext.CryptoSessionToken,
            "an unlocked session carries X-Crypto-Session on its user-data calls");
        recorder.Seen[1].Path.Should().Be(AccountClient.KdfPath,
            "the unlock flow itself is NOT X-Crypto-Session judged");
        recorder.Seen[1].SessionHeader.Should().BeNull(
            "only /v1/user/* rides the envelope (M2T6 blast-radius precedent)");
    }

    [Fact]
    public async Task CryptoSessionHandler_OmitsHeader_WhenLocked()
    {
        var store = new TokenStore(); // signed in, never unlocked (or re-locked)
        store.Set(SpaTestContext.FakeJwt(role: "User"));
        var recorder = new RecordingHandler();
        using var client = Client(store, recorder);

        using (await client.GetAsync("/v1/user/sources")) { }
        recorder.Seen.Should().ContainSingle();
        recorder.Seen[0].SessionHeader.Should().BeNull(
            "a locked vault sends no session credential at all");

        // A retry clone can arrive carrying a stale value — it must be
        // stripped rather than sent alongside (or instead of) the fresh one.
        var stale = new HttpRequestMessage(HttpMethod.Get, "/v1/user/actions");
        stale.Headers.TryAddWithoutValidation(
            CryptoSessionHandler.HeaderName, "stale-token-from-a-retry-clone");
        using (await client.SendAsync(stale)) { }

        recorder.Seen.Should().HaveCount(2);
        recorder.Seen[1].SessionHeader.Should().BeNull(
            "a stale X-Crypto-Session is stripped, never forwarded");
    }

    // ----------------------------------------------------------------- lifetime

    /// <summary>
    /// SQ-M5-1's DEFAULT: the unlock token is memory-only. A page refresh
    /// (a fresh <see cref="TokenStore"/>) re-prompts, and there is no
    /// persistence surface in either store to restore it from.
    /// </summary>
    [Fact]
    public void TokenStore_CryptoSessionToken_LostOnRefresh_NoStoragePersist()
    {
        using var ctx = new SpaTestContext();
        ctx.Store.Set(SpaTestContext.FakeJwt(role: "User"));
        ctx.Store.CryptoSession.SetToken(SpaTestContext.CryptoSessionToken);
        ctx.Store.CryptoSession.IsEmpty.Should().BeFalse("the session starts unlocked");

        // F5: the WASM app boots again with a fresh store instance.
        var afterRefresh = new TokenStore();
        afterRefresh.CryptoSession.IsEmpty.Should().BeTrue(
            "the token dies with the page — the prompt re-appears after a refresh");
        afterRefresh.AccessToken.Should().BeNull("and so does the JWT (M1T8 rule, unchanged)");

        // Structural: neither store holds anything but its lock and string
        // state — no JS-runtime handle, no storage key, no serializer.
        foreach (var type in new[] { typeof(TokenStore), typeof(CryptoSessionStore) })
        {
            type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(field => field.FieldType.Name)
                .Should().OnlyContain(
                    name => name is "Lock" or "String" or "Boolean" or "Action" or "CryptoSessionStore",
                    $"{type.Name} must have no localStorage/sessionStorage/JS-interop member — the token is RAM-only");
        }
    }

    // ----------------------------------------------------------------- helpers

    private static HttpClient Client(TokenStore store, RecordingHandler recorder)
        => new(new CryptoSessionHandler(store) { InnerHandler = recorder })
        {
            BaseAddress = new Uri("http://localhost/"),
        };

    /// <summary>Captures each request's path and X-Crypto-Session value as seen at the innermost handler.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Path, string? SessionHeader)> Seen { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryGetValues(CryptoSessionHandler.HeaderName, out var values);
            Seen.Add((
                request.RequestUri!.PathAndQuery,
                values is null ? null : string.Join("|", values)));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            });
        }
    }
}
