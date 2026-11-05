using System.Net;
using FluentAssertions;
using Xunit;

namespace EmailAgent.IntegrationTests;

/// <summary>
/// M1T1 acceptance criterion: <c>/metrics</c> (unversioned, §13.7) returns a
/// Prometheus exposition carrying <c>process_runtime_*</c> series (runtime
/// instrumentation) and OpenTelemetry instrument series (ASP.NET Core).
/// </summary>
public class MetricsEndpointTests
{
    [Fact]
    public async Task Metrics_Endpoint_ReturnsPrometheusExposition()
    {
        await using var factory = ApiTestFactory.Create();
        using var client = factory.CreateClient();

        // Warm-up: http.server.request.* metrics are recorded when a request
        // completes, so make requests until the series shows up in the
        // exposition (bounded poll — avoids racing the aggregation window on
        // loaded CI runners).
        var warmup = await client.GetAsync("/health/live");
        warmup.EnsureSuccessStatusCode();

        string body = string.Empty;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        do
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            var scrape = await client.GetAsync("/metrics");
            scrape.EnsureSuccessStatusCode();
            body = await scrape.Content.ReadAsStringAsync();
        }
        while (!body.Contains("http_server_request", StringComparison.Ordinal)
            && DateTime.UtcNow < deadline);

        var response = await client.GetAsync("/metrics");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("# HELP");
        body.Should().Contain("# TYPE");
        body.Should().Contain("process_runtime_");
        body.Should().Contain("http_server_request");
    }
}
