using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EmailAgent.Api.Auth;
using EmailAgent.IntegrationTests.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmailAgent.IntegrationTests.M1EndToEnd;

// ---------------------------------------------------------------------------
// M1T9 (task step 5): six login attempts from the same remote IP inside one
// fixed window. Calls 1–5 must NOT be 429 (401 for a wrong password or 200
// for a right one); call 6 must be 429 with Retry-After (Q-O7 / §13.14).
// AccountTestStartupFilter's X-Test-IP header overrides
// Connection.RemoteIpAddress so each run targets a distinct per-IP bucket.
// ---------------------------------------------------------------------------

/// <summary>
/// Q-O7 login budget: 5/min per IP. The sixth attempt in the same window is
/// rejected with RFC 7807 429 + <c>Retry-After</c> and without
/// <c>X-RateLimit-*</c> (unauthenticated responses, §13.14).
/// </summary>
[Trait("Category", "M1EndToEnd")]
[Collection("Postgres")]
public class RateLimitTests
{
    private readonly PostgresFixture _postgres;

    public RateLimitTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task SixLoginsPerMinute_PerIP_SixthReturns429_WithRetryAfter()
    {
        var connectionString = await AccountTestHost.MigrateAsync(_postgres);
        await using var factory = AccountTestHost.CreateFactory(connectionString);
        using var client = AccountTestHost.CreateClient(factory);

        // Start inside a fresh epoch-aligned 60 s window so the whole burst
        // cannot straddle a window boundary (review finding 3a pattern).
        await AccountTestHost.WaitForFreshRateLimitWindowAsync();

        // A dedicated IP keeps this burst isolated from every other suite's
        // login attempts in the shared collection database / host process.
        var ip = $"198.51.100.{Random.Shared.Next(1, 200)}";

        var email = $"m1t9-ratelimit-{Guid.NewGuid():N}@example.test";
        await AccountTestHost.SeedUserAsync(factory, email);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < RateLimitPolicies.LoginPerMinute; i++)
        {
            var attempt = await AccountTestHost.PostLoginAsync(
                client, email, AccountTestHost.WrongPassword, ip);
            statuses.Add(attempt.StatusCode);
            attempt.StatusCode.Should().NotBe(
                HttpStatusCode.TooManyRequests,
                $"attempt {i + 1} is within the {RateLimitPolicies.LoginPerMinute}/min budget");
            // Wrong password is a typed 401 — not a limit rejection.
            attempt.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var rejected = await AccountTestHost.PostLoginAsync(
            client, email, AccountTestHost.WrongPassword, ip);
        rejected.StatusCode.Should().Be(
            HttpStatusCode.TooManyRequests, "the sixth attempt in the window is rejected");
        rejected.Headers.RetryAfter.Should().NotBeNull(
            "429s carry Retry-After (§13.14)");
        rejected.Headers.Contains("X-RateLimit-Limit").Should().BeFalse(
            "unauthenticated login responses do not carry X-RateLimit-* (§13.14)");

        var problem = await AccountTestHost.ReadProblemAsync(
            rejected, HttpStatusCode.TooManyRequests);
        problem.GetProperty("type").GetString().Should().Be("/errors/rate-limited");
        problem.GetProperty("status").GetInt32().Should().Be(429);
    }
}