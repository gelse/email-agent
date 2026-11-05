using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using EmailAgent.Api.Auth;

namespace EmailAgent.IntegrationTests.Auth;

/// <summary>
/// M1T4 test-endpoint plumbing: M1T6's login endpoints do not exist yet, so
/// these factory-only routes opt into the real named policies (5/min 429
/// envelope) and the real authorization middleware so the §13.14 header rule
/// can be exercised over HTTP today.
///
/// Registered via <c>WithWebHostBuilder(b =&gt; b.ConfigureServices(...))</c>
/// as an <see cref="IStartupFilter"/> — <c>IWebHostBuilder.Configure</c>'s
/// delegate is registered before the entry point's own and gets overwritten
/// for minimal-hosting apps (the observed 404s), whereas service
/// registrations always land. The filter wraps the application pipeline:
/// pre-<c>next</c> routing claims the route builder for the test routes
/// (before the app's own UseRouting can set an endpoint), post-<c>next</c>
/// UseEndpoints maps them as the innermost terminal — so the application's
/// authentication, authorization and rate-limiter middleware all observe the
/// matched endpoint metadata first.
/// </summary>
internal sealed class TestEndpointsStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            // Diagnostic marker: proves the filter ran for this factory (a
            // 404 without the header means ConfigureServices did not land).
            app.Use((context, nextMiddleware) =>
            {
                context.Response.Headers["X-M1T4-TestEndpoints"] = "1";
                return nextMiddleware();
            });

            // Pre: match only the test routes, before the application wires
            // its own routing (EndpointRoutingMiddleware skips matching when
            // an endpoint is already set, so this never steals app routes).
            app.UseRouting();

            next(app);

            // Post: map as the innermost terminal — reached only when the
            // application pipeline did not handle the request itself.
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapPost("/__m1t4/login-per-ip", () => Results.Ok())
                    .RequireRateLimiting(RateLimitPolicies.LoginPerIp);
                endpoints.MapPost("/__m1t4/login-per-account", () => Results.Ok())
                    .RequireRateLimiting(RateLimitPolicies.LoginPerAccount);
                endpoints.MapGet("/__m1t4/authenticated", () => Results.Ok())
                    .RequireAuthorization();
            });
        };
    }
}
