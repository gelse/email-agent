using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmailAgent.Core.Audit;
using EmailAgent.Core.Options;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Plugins;
using EmailAgent.IntegrationTests.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace EmailAgent.IntegrationTests.M2EndToEnd;

/// <summary>
/// Smoke flow 3 of M2T7 as an xUnit fact: a byte-flipped
/// <c>EmailAgent.Connectors.Imap.dll</c> whose companion manifest claims the
/// DEFAULT plugin_id <c>imap</c> is refused at load time
/// (reason <c>signature_mismatch</c>), refusal-wins then unregisters the
/// claimed id, and the API surface agrees — <c>test-connection</c> answers
/// RFC 7807 <c>/errors/plugin-not-registered</c> instead of silently reaching
/// a plugin the operator just tampered with.
/// </summary>
/// <remarks>
/// The M2T3-level sibling of this fact
/// (<c>PluginLoaderTests.Loader_Unregisters_Default_Plugin_Whose_Id_A_Refused_DropIn_Claims</c>)
/// proves the registry + audit halves; this M2E2E fact adds the HTTP half the
/// smoke script asserts (smoke-m2.sh flow 3) against a fresh factory-hosted
/// host. The tampered artifact is generated per test from the shipped
/// connector's bytes in its own temp drop folder — no binary fixture is
/// committed, and no production endpoint is called (AGENTS.md hard constraint).
/// </remarks>
[Trait("Category", "M2EndToEnd")]
[Collection(M2E2ECollection.Name)]
public sealed class PluginLoaderTamperTests
{
    private const string CompanionDll = "Fabricated.Plugin.dll";

    /// <summary>
    /// M3T1: the DEFAULT <c>IMessageSource</c> ids after the imap refusal
    /// (the OpenAI-compatible DEFAULT plugin now occupies the IAgentEngine
    /// slot, so it never appears in <c>MessageSourceIds</c>).
    /// </summary>
    private static readonly string[] RemainingDefaultPluginIds =
        { "telegram-send" };

    private readonly PostgresFixture _postgres;

    public PluginLoaderTamperTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task TamperedPlugin_RefusedAtLoadTime_AndRegistryReturnsNull()
    {
        var connectionString = await AccountTestHost.MigrateAsync(_postgres);
        var signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var directory = CreatePluginsDirectory();
        var bytes = ImapAssemblyBytes();

        // The smoke flow 3 artifact: signature computed over the pristine
        // bytes + manifest, planted DLL differs by one flipped byte, and the
        // companion claims plugin_id "imap" (v9.9.9) so the refusal keys on
        // the DEFAULT id.
        var tampered = (byte[])bytes.Clone();
        tampered[^1] ^= 0xFF;
        PlantPlugin(directory, "imap", "9.9.9", tampered, signingKey, bytesToSign: bytes);

        await using var factory = ApiTestFactory.Create(connectionString, directory, signingKey);
        var client = factory.CreateClient(); // host start — the startup scan runs the refusal

        // (a) Load-time refusal: the build-output imap did not survive the scan.
        var registry = factory.Services.GetRequiredService<IPluginRegistry>();
        registry.GetMessageSource("imap").Should().BeNull(
            "refusal-wins: the refused drop-in claimed plugin_id imap, "
            + "so the good build-output imap must not remain registered");
        registry.MessageSourceIds.Should().BeEquivalentTo(
            RemainingDefaultPluginIds,
            "only the DEFAULT plugins whose ids were not poisoned by the refusal remain");

        // (b) The audited refusal the smoke script greps in the api logs —
        // reason=signature_mismatch and the plugin_id that keys the unregister.
        var rejection = await FindAuditMetadataAsync(
            factory,
            AuditActions.PluginRejected,
            PluginSignatureException.SignatureMismatch,
            CompanionDll,
            "imap");
        rejection.Should().NotBeNull(
            "the startup scan must write plugin_rejected/signature_mismatch with plugin_id=imap");
        ParseMetadata(rejection!).GetProperty("detail").GetString().Should().Be("signature");

        // (c) The API surface agrees: no socket is opened, the request fails
        // with the RFC 7807 type the smoke script pins for flow 3.
        var (_, bearer, _) = await SourcesTestHost.AddUserAsync(factory);
        using var response = await client.SendAsync(SourcesTestHost.Request(
            HttpMethod.Post, "/v1/user/sources/test-connection", bearer,
            new { type_id = "imap", config = SourcesTestHost.Config() }));

        (await AccountTestHost.ReadProblemAsync(response, HttpStatusCode.BadRequest))
            .GetProperty("type").GetString().Should().Be("/errors/plugin-not-registered");
    }

    // -----------------------------------------------------------------------
    // Helpers — minimal parity copies of PluginLoaderTests' private helpers
    // (kept local so the M2T3 suite's surface stays untouched).
    // -----------------------------------------------------------------------

    /// <summary>Per-test drop folder so this scan can never see another test's candidates.</summary>
    private static string CreatePluginsDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "email-agent-tests",
            "m2e2e-tamper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static byte[] ImapAssemblyBytes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "EmailAgent.Connectors.Imap.dll");
        File.Exists(path).Should().BeTrue(
            "the IMAP connector must ride along into the test output via the EmailAgent.Api project reference (§5)");
        return File.ReadAllBytes(path);
    }

    private static PluginSignatureVerifier CreateVerifier(string signingKey) =>
        new(Options.Create(new PluginOptions { SigningKey = signingKey }));

    /// <summary>The base64 signature over (assembly bytes, the companion manifest text as written).</summary>
    private static string Sign(byte[] assemblyBytes, string manifestText, string signingKey) =>
        Convert.ToBase64String(CreateVerifier(signingKey).ComputeSignature(assemblyBytes, manifestText));

    /// <summary>
    /// The companion manifest WITHOUT a <c>signature</c> member — the exact
    /// text the HMAC covers (the verifier removes <c>signature</c> before
    /// canonicalizing, but it must be absent at signing time).
    /// </summary>
    private static string BuildManifest(string pluginId, string version)
    {
        var manifest = new Dictionary<string, object?>
        {
            ["plugin_id"] = pluginId,
            ["version"] = version,
            ["assembly"] = CompanionDll,
            ["type"] = "EmailAgent.Connectors.Imap.ImapMessageSource",
            ["capabilities"] = new[] { "processed_messages_write" },
            ["http_allowlist"] = Array.Empty<string>(),
            ["notes"] = "M2T7 integration-test tampered drop-in (not a shipped plugin).",
        };
        return JsonSerializer.Serialize(manifest);
    }

    /// <summary>Inserts the <c>signature</c> member into an existing manifest text.</summary>
    private static string WithSignature(string manifestText, string signatureBase64)
    {
        var manifest = JsonNode.Parse(manifestText)?.AsObject()
            ?? throw new InvalidOperationException("test manifest is not a JSON object");
        manifest["signature"] = signatureBase64;
        return manifest.ToJsonString();
    }

    /// <summary>
    /// Plants the tampered drop-in: flipped DLL bytes plus a companion
    /// <c>Fabricated.Plugin.plugin.json</c> signed over the PRISTINE bytes.
    /// </summary>
    private static void PlantPlugin(
        string directory,
        string pluginId,
        string version,
        byte[] assemblyBytes,
        string signingKey,
        byte[] bytesToSign)
    {
        var manifestText = BuildManifest(pluginId, version);
        var companionText = WithSignature(manifestText, Sign(bytesToSign, manifestText, signingKey));

        File.WriteAllBytes(Path.Combine(directory, CompanionDll), assemblyBytes);
        File.WriteAllText(
            Path.Combine(directory, Path.GetFileNameWithoutExtension(CompanionDll) + ".plugin.json"),
            companionText);
    }

    /// <summary>
    /// First <c>metadata_json</c> of an <c>audit_log</c> row matching the
    /// action plus reason / assembly file / plugin_id — the shared container
    /// accumulates rows, so every assertion filters explicitly.
    /// </summary>
    private static async Task<string?> FindAuditMetadataAsync(
        WebApplicationFactory<Program> factory,
        string action,
        string reason,
        string assemblyFile,
        string pluginId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var rows = await db.AuditLog.AsNoTracking()
            .Where(row => row.Action == action)
            .Select(row => row.MetadataJson)
            .ToListAsync();

        foreach (var json in rows)
        {
            if (string.IsNullOrEmpty(json))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(json);
            var metadata = doc.RootElement;
            if (!Matches(metadata, "reason", reason)
                || !Matches(metadata, "assembly_file", assemblyFile)
                || !Matches(metadata, "plugin_id", pluginId))
            {
                continue;
            }

            return json;
        }

        return null;

        static bool Matches(JsonElement metadata, string property, string expected)
            => metadata.TryGetProperty(property, out var element)
               && string.Equals(element.GetString(), expected, StringComparison.Ordinal);
    }

    private static JsonElement ParseMetadata(string metadataJson)
    {
        using var doc = JsonDocument.Parse(metadataJson);
        return doc.RootElement.Clone();
    }
}
