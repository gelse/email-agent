using System.Net;
using System.Text;
using Bunit;
using EmailAgent.Spa.Actions;
using EmailAgent.Spa.Auth;
using EmailAgent.Spa.Models;
using EmailAgent.Spa.Notifications;
using EmailAgent.Spa.NotificationsApi;
using EmailAgent.Spa.Security;
using EmailAgent.Spa.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace EmailAgent.UnitTests.Spa;

/// <summary>
/// Shared M1T8 bUnit harness: one <see cref="TestContext"/> (bUnit 1.x —
/// <c>Services</c> is a <c>TestServiceProvider</c> acting as both collection
/// and provider) with the SPA's token store, role parser and view-once state
/// registered, plus a programmable stub <see cref="HttpMessageHandler"/>
/// standing in for the API (no test ever reaches a real service — AGENTS.md
/// hard constraint 3). M2T6 adds the toast bus and the typed
/// <see cref="SourcesClient"/> over the same stub, M3T5 the typed
/// <see cref="ModelsClient"/>, M3T7 the typed <see cref="UserModelsClient"/>,
/// all mirroring Program.cs.
/// </summary>
internal sealed class SpaTestContext : TestContext
{
    public SpaTestContext()
    {
        Services.AddSingleton<TokenStore>();
        Services.AddSingleton<JwtRoleParser>();
        Services.AddScoped<OtpModalState>();
        Services.AddSingleton(_ => new HttpClient(Handler)
        {
            BaseAddress = new Uri("http://localhost/"),
        });

        // M2T6: same registrations as Program.cs — one toast queue per test
        // and the sources client bound to the stubbed "Api" HttpClient.
        // M3T5: the models client over that same stub (Program.cs parity).
        Services.AddSingleton<ToastService>();
        Services.AddScoped(sp => new SourcesClient(sp.GetRequiredService<HttpClient>()));
        Services.AddScoped(sp => new ModelsClient(sp.GetRequiredService<HttpClient>()));
        // M3T7: the /user/agents/new picker's typed client over the same stub
        // (Program.cs parity — one HttpClient, one transport per test).
        Services.AddScoped(sp => new UserModelsClient(sp.GetRequiredService<HttpClient>()));
        // M4T4: actions + notifications clients over the same stub.
        Services.AddScoped(sp => new ActionsClient(sp.GetRequiredService<HttpClient>()));
        Services.AddScoped(sp => new NotificationsClient(sp.GetRequiredService<HttpClient>()));

        // M5T1: same registrations as Program.cs — the account client over
        // the stub, the browser-crypto seam faked deterministically (bUnit
        // has no JS runtime) and the unlock orchestrator on top of both.
        Services.AddScoped(sp => new AccountClient(sp.GetRequiredService<HttpClient>()));
        Services.AddSingleton<IUnlockCrypto>(_ => FakeCrypto);
        Services.AddScoped(sp => new UnlockService(
            sp.GetRequiredService<AccountClient>(),
            sp.GetRequiredService<IUnlockCrypto>(),
            sp.GetRequiredService<TokenStore>()));
    }

    /// <summary>Opaque crypto-session token used by the M5T1 unit tests.</summary>
    public const string CryptoSessionToken = "m5t1-session-token";

    /// <summary>
    /// The deterministic <see cref="IUnlockCrypto"/> every test shares: it
    /// records each step's inputs (copied, because <see cref="UnlockService"/>
    /// zeroes the KEK/DEK buffers afterwards) and returns a known 32-byte
    /// KEK/DEK pair so tests can assert the exact bytes that hit the wire.
    /// </summary>
    public FakeUnlockCrypto FakeCrypto { get; } = new();

    /// <summary>The stub transport every page's injected HttpClient uses.</summary>
    public StubHttpHandler Handler { get; } = new();

    /// <summary>The resolved singleton token store (same instance the pages see).</summary>
    public TokenStore Store => Services.GetRequiredService<TokenStore>();

    /// <summary>The toast bus the components raise (same instance the pages see).</summary>
    public ToastService Toasts => Services.GetRequiredService<ToastService>();

    /// <summary>bUnit's fake NavigationManager (records NavigateTo targets).</summary>
    public Microsoft.AspNetCore.Components.NavigationManager Nav
        => Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();

    /// <summary>
    /// Builds an unsigned compact JWT carrying the given role claim — the
    /// SPA parses (never verifies) this for routing only (task Risks).
    /// </summary>
    public static string FakeJwt(string? role = "Admin", string? email = null)
    {
        var header = B64Url("{\"alg\":\"HS256\",\"typ\":\"JWT\"}");
        var payloadJson = email is null
            ? $"{{\"sub\":\"11111111-1111-1111-1111-111111111111\"{(role is null ? "" : $",\"role\":\"{role}\"")}}}"
            : $"{{\"sub\":\"11111111-1111-1111-1111-111111111111\",\"email\":\"{email}\"{(role is null ? "" : $",\"role\":\"{role}\"")}}}";
        return $"{header}.{B64Url(payloadJson)}.c2ln";

        static string B64Url(string value)
            => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
    }

    /// <summary>An RFC 7807 response for error-path stubs.</summary>
    public static HttpResponseMessage Problem(HttpStatusCode status, string detail)
        => new(status)
        {
            Content = new StringContent(
                $"{{\"type\":\"/errors/unauthorized\",\"title\":\"Unauthorized\",\"status\":{(int)status},\"detail\":\"{detail}\"}}",
                Encoding.UTF8,
                "application/problem+json"),
        };

    /// <summary>A JSON response for happy-path stubs.</summary>
    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    public static HttpResponseMessage NoContent(HttpStatusCode status = HttpStatusCode.NoContent)
        => new(status);
}

/// <summary>
/// Records every outbound request (method, path, body) and answers through a
/// per-test responder function — the only HTTP surface the SPA tests see.
/// </summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    public sealed record RecordedRequest(string Method, string Path, string? Body);

    private readonly List<RecordedRequest> _requests = new();

    /// <summary>Answer for the next request; defaults to 404 so misses fail loudly.</summary>
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Responder { get; set; }
        = (_, _) => Task.FromResult(SpaTestContext.Json(
            "{\"type\":\"/errors/not-found\",\"title\":\"Not Found\",\"status\":404,\"detail\":\"stub not configured\"}",
            HttpStatusCode.NotFound));

    public IReadOnlyList<RecordedRequest> Requests => _requests;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        _requests.Add(new RecordedRequest(
            request.Method.Method,
            request.RequestUri?.PathAndQuery ?? string.Empty,
            body));
        return await Responder(request, cancellationToken);
    }
}

/// <summary>
/// M5T1: the fake <see cref="IUnlockCrypto"/> standing in for the browser's
/// Argon2id + <c>crypto.subtle</c>. It records the inputs of every step (as
/// copies — <see cref="UnlockService"/> zeroes the KEK/DEK in a finally) and
/// answers with known 32-byte keys, so a test can assert the derivation used
/// the server's stored material and that the unwrapped DEK — and only the
/// DEK, never the passphrase — reached the unlock POST.
/// </summary>
internal sealed class FakeUnlockCrypto : IUnlockCrypto
{
    /// <summary>KEK this fake "derives" (32 bytes, distinguishable from the DEK).</summary>
    public static readonly byte[] Kek =
        Enumerable.Range(0, 32).Select(i => (byte)(0xA0 + i)).ToArray();

    /// <summary>DEK this fake "unwraps"; the unlock POST body must carry exactly this, base64.</summary>
    public static readonly byte[] Dek =
        Enumerable.Range(0, 32).Select(i => (byte)(0x10 + i)).ToArray();

    /// <summary>The passphrase handed to DeriveKek (tests assert it never hits the wire).</summary>
    public string? DerivedPassphrase { get; private set; }

    /// <summary>The salt handed to DeriveKek (copy).</summary>
    public byte[]? DerivedSalt { get; private set; }

    /// <summary>The KDF params handed to DeriveKek (copy of the record).</summary>
    public KdfParamsDto? DerivedParams { get; private set; }

    /// <summary>The KEK handed to UnwrapDek (copy — must equal <see cref="Kek"/> before zeroing).</summary>
    public byte[]? UnwrappedKek { get; private set; }

    /// <summary>The wrapped DEK handed to UnwrapDek (copy).</summary>
    public byte[]? UnwrappedWrapped { get; private set; }

    /// <summary>The nonce handed to UnwrapDek (copy).</summary>
    public byte[]? UnwrappedNonce { get; private set; }

    /// <summary>When set, UnwrapDek throws it instead (wrong-passphrase path).</summary>
    public Exception? UnwrapFailure { get; set; }

    /// <summary>When set, DeriveKek throws it instead (interop-failure path).</summary>
    public Exception? DeriveFailure { get; set; }

    public Task<byte[]> DeriveKekAsync(
        string passphrase,
        byte[] salt,
        KdfParamsDto kdfParams,
        CancellationToken ct = default)
    {
        if (DeriveFailure is not null)
        {
            return Task.FromException<byte[]>(DeriveFailure);
        }

        DerivedPassphrase = passphrase;
        DerivedSalt = salt.ToArray();
        DerivedParams = kdfParams; // init-only DTO — safe to hold the reference
        return Task.FromResult(Kek.ToArray());
    }

    public Task<byte[]> UnwrapDekAsync(
        ReadOnlyMemory<byte> kek,
        byte[] dekWrapped,
        byte[] dekNonce,
        CancellationToken ct = default)
    {
        UnwrappedKek = kek.ToArray();
        UnwrappedWrapped = dekWrapped.ToArray();
        UnwrappedNonce = dekNonce.ToArray();
        if (UnwrapFailure is not null)
        {
            return Task.FromException<byte[]>(UnwrapFailure);
        }

        return Task.FromResult(Dek.ToArray());
    }
}
