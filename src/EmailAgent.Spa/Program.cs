using EmailAgent.Spa;
using EmailAgent.Spa.Auth;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// §13.9 same-origin dev topology: nginx serves this bundle AND reverse-proxies
// /v1/* on one origin, so the httpOnly ea_refresh cookie (SameSite=Lax,
// Path=/v1/account/refresh) flows without any CORS setup. Q-A13: that origin
// is the dev TLS edge, https://localhost:8443 (self-signed, same-origin with
// the api through nginx); wwwroot/appsettings.json carries ApiBaseUrl and the
// fallback matches it. A bundle loaded from any other origin would need its
// own ApiBaseUrl (the compose stack always serves the SPA from the edge).
var apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? "https://localhost:8443";

// M1T8 services: the in-memory token store (singleton, §13.10 — never
// localStorage), the unsigned role parser for routing, the auth state over
// the token store, and the view-once OTP modal state (scoped).
builder.Services.AddSingleton<TokenStore>();
builder.Services.AddSingleton<JwtRoleParser>();
builder.Services.AddScoped<OtpModalState>();
builder.Services.AddScoped<AuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(
    sp => sp.GetRequiredService<AuthStateProvider>());
builder.Services.AddAuthorizationCore(options =>
{
    options.AddPolicy("Admin", p => p.RequireRole("Admin"));
    options.AddPolicy("User", p => p.RequireRole("User", "Admin"));
});

// "Api" client: bearer header from the token store; on 401 one silent
// refresh through the cookie path, then a single retry (BearerTokenHandler).
builder.Services.AddTransient<BearerTokenHandler>();
builder.Services.AddHttpClient("Api", c => c.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<BearerTokenHandler>();
// Pages inject the default HttpClient — bound to the "Api" configuration.
builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("Api"));

// "RefreshClient": cookie-only (no bearer handler) so the rotation response
// and request carry just the ea_refresh cookie via the browser's same-origin
// credentials. RefreshCookieAuthenticator wraps it (serialized, §13.10).
builder.Services.AddHttpClient(RefreshCookieAuthenticator.ClientName,
    c => c.BaseAddress = new Uri(apiBaseUrl));
builder.Services.AddSingleton<RefreshCookieAuthenticator>();

var host = builder.Build();

// Task step 4: one silent refresh before the router renders — a page reload
// with a still-valid refresh cookie restores the session in place; otherwise
// the SPA simply starts anonymous and gated routes redirect to /account/login.
await host.Services.GetRequiredService<RefreshCookieAuthenticator>()
    .AttemptSilentRefreshAsync();

await host.RunAsync();