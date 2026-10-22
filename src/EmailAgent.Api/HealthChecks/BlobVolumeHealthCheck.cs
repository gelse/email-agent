using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EmailAgent.Api.HealthChecks;

public sealed class BlobVolumeHealthCheck : IHealthCheck
{
    private readonly IConfiguration _configuration;

    public BlobVolumeHealthCheck(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var path = _configuration["Blobs:Path"]
            ?? _configuration["Blobs__Path"];

        if (string.IsNullOrWhiteSpace(path))
        {
            return HealthCheckResult.Unhealthy("Blobs__Path is not configured.");
        }

        try
        {
            Directory.CreateDirectory(path);

            var probeFile = Path.Combine(path, $".health-probe-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(probeFile, "ok", cancellationToken);
            var readBack = await File.ReadAllTextAsync(probeFile, cancellationToken);
            File.Delete(probeFile);

            if (readBack != "ok")
            {
                return HealthCheckResult.Unhealthy("Blob volume round-trip verification failed.");
            }

            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Blob volume is not writable.", ex);
        }
    }
}
