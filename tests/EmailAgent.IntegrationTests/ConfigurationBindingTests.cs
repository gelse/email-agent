using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmailAgent.IntegrationTests;

/// <summary>
/// M1T1 acceptance criterion: the configuration binder accepts every
/// <c>AUTH__*</c>, <c>AUDIT__*</c>, <c>SYSTEM__*</c>, <c>OTP__*</c>,
/// <c>SPA__*</c> and <c>API__*</c> environment variable without throwing.
/// The test boots the real host with those environment variables set and
/// asserts that the env-var layer overrides the appsettings.json defaults.
/// </summary>
[CollectionDefinition("EnvironmentVariables", DisableParallelization = true)]
public sealed class EnvironmentVariablesCollection
{
}

[Collection("EnvironmentVariables")]
public class ConfigurationBindingTests
{
    private static readonly Dictionary<string, string?> M1EnvVars = new()
    {
        ["AUTH__SigningKey"] = "test-signing-key-0123456789-0123456789",
        ["AUTH__Issuer"] = "email-agent-env-test",
        ["AUTH__Audience"] = "email-agent-spa-env-test",
        ["AUTH__AccessTokenTtl"] = "00:05:00",
        ["AUTH__RefreshTokenTtl"] = "2.00:00:00",
        ["AUDIT__HmacKey"] = "test-audit-hmac-key-0123456789-012345",
        ["SYSTEM__KeyEncryptionKey"] = "test-system-kek-0123456789-0123456",
        ["OTP__Lifetime"] = "1.00:00:00",
        ["SPA__AllowedOrigins"] = "",
        ["API__PublicBaseUrl"] = "http://localhost:8080",
    };

    [Fact]
    public async Task Configuration_AuthAuditSystemOtpSpaApiEnvVars_BindWithoutThrowing()
    {
        var previous = M1EnvVars.Keys.ToDictionary(
            key => key,
            key => Environment.GetEnvironmentVariable(key));

        try
        {
            foreach (var (name, value) in M1EnvVars)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            var config = factory.Services.GetRequiredService<IConfiguration>();

            var auth = config.GetSection("Auth").Get<AuthOptions>();
            auth.Should().NotBeNull();
            auth!.SigningKey.Should().Be(M1EnvVars["AUTH__SigningKey"]);
            auth.Issuer.Should().Be("email-agent-env-test");
            auth.Audience.Should().Be("email-agent-spa-env-test");
            auth.AccessTokenTtl.Should().Be(TimeSpan.FromMinutes(5));
            auth.RefreshTokenTtl.Should().Be(TimeSpan.FromDays(2));

            var audit = config.GetSection("Audit").Get<AuditOptions>();
            audit.Should().NotBeNull();
            audit!.HmacKey.Should().Be(M1EnvVars["AUDIT__HmacKey"]);

            var system = config.GetSection("System").Get<SystemOptions>();
            system.Should().NotBeNull();
            system!.KeyEncryptionKey.Should().Be(M1EnvVars["SYSTEM__KeyEncryptionKey"]);

            var otp = config.GetSection("Otp").Get<OtpOptions>();
            otp.Should().NotBeNull();
            otp!.Lifetime.Should().Be(TimeSpan.FromDays(1));

            var spa = config.GetSection("Spa").Get<SpaOptions>();
            spa.Should().NotBeNull();
            spa!.AllowedOrigins.Should().BeEmpty();

            var api = config.GetSection("Api").Get<ApiOptions>();
            api.Should().NotBeNull();
            api!.PublicBaseUrl.Should().Be("http://localhost:8080");
        }
        finally
        {
            foreach (var (name, value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    /// <summary>Local bind targets mirroring the §16 env-var contract.</summary>
    public sealed class AuthOptions
    {
        public string? SigningKey { get; set; }
        public string? Issuer { get; set; }
        public string? Audience { get; set; }
        public TimeSpan AccessTokenTtl { get; set; }
        public TimeSpan RefreshTokenTtl { get; set; }
    }

    public sealed class AuditOptions
    {
        public string? HmacKey { get; set; }
    }

    public sealed class SystemOptions
    {
        public string? KeyEncryptionKey { get; set; }
    }

    public sealed class OtpOptions
    {
        public TimeSpan Lifetime { get; set; }
    }

    public sealed class SpaOptions
    {
        public string? AllowedOrigins { get; set; }
    }

    public sealed class ApiOptions
    {
        public string? PublicBaseUrl { get; set; }
    }
}
