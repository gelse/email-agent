using System.Net;
using System.Text;
using Bunit;
using EmailAgent.Spa.Auth;
using EmailAgent.Spa.Models;
using EmailAgent.Spa.Notifications;
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
/// <see cref="ModelsClient"/>, both mirroring Program.cs.
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
    }

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
