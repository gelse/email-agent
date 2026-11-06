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
    /// <summary>
    /// Builds a factory-hosted host with per-factory temp paths and the
    /// throwaway M1/M2 test key literals (see below) pre-set.
    /// </summary>
    /// <param name="connectionString">Postgres connection string, or null for DB-less hosts.</param>
    /// <param name="pluginsDirectory">Drop folder the M2T3 loader scans, or null for a unique (usually absent) temp path.</param>
    /// <param name="pluginSigningKey">
    /// M2T3 <c>Plugin:SigningKey</c>; defaults to the shared throwaway test
    /// literal for hosts that do not sign anything. PluginLoaderTests passes
    /// its OWN random per-test key so its candidates are signed with material
    /// no other test can predict (hermetic; test fixture only — never a real
    /// secret, same rule as the M1 Auth/Audit literals above).
    /// </param>
    public static WebApplicationFactory<Program> Create(
        string? connectionString = null,
        string? pluginsDirectory = null,
        string? pluginSigningKey = null)
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
            // M2T3: Plugin:SigningKey is ValidateOnStart and the PluginLoader's
            // PluginSignatureVerifier validates it in its constructor, so every
            // factory-hosted host must boot with a >= 32-byte test key or the
            // host refuses to start. Throwaway test literal only, never a real
            // secret; PluginLoaderTests overrides this with its own random key.
            builder.UseSetting(
                "Plugin:SigningKey",
                pluginSigningKey ?? "m2t3-integration-test-plugin-signing-key-0123456789");
            // M2T3: each factory gets its own (usually not yet existing)
            // drop-folder so host-startup plugin scans in one test can never
            // see another test's candidates; PluginLoaderTests passes its own
            // pre-created directory to plant candidates before host start.
            builder.UseSetting(
                "Plugin:PluginsDirectory",
                pluginsDirectory ?? Path.Combine(
                    Path.GetTempPath(),
                    "email-agent-tests",
                    "plugins-" + Guid.NewGuid().ToString("N")));
            if (connectionString is not null)
            {
                builder.UseSetting("ConnectionStrings:Postgres", connectionString);
            }
        });
    }
}
