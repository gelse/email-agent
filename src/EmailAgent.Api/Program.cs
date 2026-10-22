using EmailAgent.Api.HealthChecks;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) =>
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddPrometheusExporter());

builder.Services.AddHealthChecks()
    .AddCheck<DbPingHealthCheck>("db", tags: new[] { "ready" })
    .AddCheck<BlobVolumeHealthCheck>("blob-volume", tags: new[] { "ready" });

var app = builder.Build();

app.UseSerilogRequestLogging();

app.MapGet("/health/live", () => Results.Ok());

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = ReadyHealthCheckResponseWriter.WriteResponse,
});

app.MapPrometheusScrapingEndpoint("/metrics");

app.Run();
