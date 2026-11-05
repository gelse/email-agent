using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EmailAgent.IntegrationTests;

/// <summary>
/// Factory helpers for the M1T1 health/metrics tests. Every factory points
/// <c>Blobs:Path</c> at a unique per-factory temp directory so the
/// blob-volume readiness check never depends on the host path
/// (cross-platform — AGENTS.md hard constraint 1), and optionally overrides
/// the Postgres connection string.
/// </summary>
internal static class ApiTestFactory
{
    public static WebApplicationFactory<Program> Create(string? connectionString = null)
    {
        var blobPath = Path.Combine(
            Path.GetTempPath(),
            "email-agent-tests",
            Guid.NewGuid().ToString("N"));

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Blobs:Path", blobPath);
            // M1T3: SYSTEM__KeyEncryptionKey (System:KeyEncryptionKey) is
            // validated at host start (ValidateOnStart), so every factory-
            // hosted test must supply an ephemeral >= 32-byte key or the host
            // refuses to start. Test literal only — never a real secret.
            builder.UseSetting(
                "System:KeyEncryptionKey",
                "email-agent-integration-test-system-kek-0123456789");
            // M1T4: Audit:HmacKey is ValidateOnStart (AddAuth) and
            // Auth:SigningKey is required to issue/validate JWTs — every
            // factory-hosted test must boot with both. Test literals only
            // (≥ 32 bytes), never real secrets (AGENTS.md).
            builder.UseSetting(
                "Audit:HmacKey",
                "m1t4-integration-test-audit-hmac-key-0123456789");
            builder.UseSetting(
                "Auth:SigningKey",
                "m1t4-integration-test-signing-key-0123456789");
            if (connectionString is not null)
            {
                builder.UseSetting("ConnectionStrings:Postgres", connectionString);
            }
        });
    }
}