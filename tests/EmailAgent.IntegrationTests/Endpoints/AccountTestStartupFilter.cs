using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EmailAgent.IntegrationTests.Endpoints;

/// <summary>
/// M1T6 test-only pipeline additions, registered through
/// <c>ConfigureServices</c> → <see cref="IStartupFilter"/> (the same
/// mechanism as M1T4's <c>TestEndpointsStartupFilter</c>):
/// </summary>
/// <remarks>
/// <para>
/// 1. <b>Remote-IP override.</b> TestServer leaves
/// <c>Connection.RemoteIpAddress</c> null while production always has one
/// (Kestrel). This filter defaults it to loopback — so audit rows carry a
/// non-null <c>ip</c> like production — and honors an <c>X-Test-IP</c>
/// request header so the Q-O7 per-IP budget can be exercised independently
/// of the per-account budget (six requests, six addresses).
/// </para>
/// <para>
/// 2. <b><c>/__m1t6/throw</c>.</b> A deliberately throwing route so the
/// M1T6 AC "every 5xx is RFC 7807" can be asserted end to end through the
/// host's exception handler. Test-only — never part of the production
/// pipeline.
/// </para>
/// </remarks>
internal sealed class AccountTestStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue("X-Test-IP", out var header)
                    && IPAddress.TryParse(header.ToString(), out var parsed))
                {
                    context.Connection.RemoteIpAddress = parsed;
                }
                else
                {
                    context.Connection.RemoteIpAddress ??= IPAddress.Loopback;
                }

                return nextMiddleware();
            });

            // Same routing arrangement as M1T4's filter: claim the route
            // builder pre-next (the application's own routing then skips —
            // observed behaviour behind M1T4's passing suite), and map the
            // test route so it is reachable alongside the real endpoints.
            app.UseRouting();

            next(app);

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/__m1t6/throw", Boom);
            });
        };
    }

    private static IResult Boom() => throw new InvalidOperationException("m1t6-test-boom");
}
