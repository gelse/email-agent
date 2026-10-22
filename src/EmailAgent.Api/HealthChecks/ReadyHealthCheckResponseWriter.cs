using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EmailAgent.Api.HealthChecks;

public static class ReadyHealthCheckResponseWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static Task WriteResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        var checks = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in report.Entries)
        {
            checks[entry.Key] = entry.Value.Status.ToString();
        }

        var payload = new
        {
            status = report.Status.ToString(),
            checks,
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload, SerializerOptions));
    }
}
