using System.Security.Cryptography;
using System.Text.Json;
using EmailAgent.Contracts.Plugins;
using EmailAgent.Infrastructure.Plugins;
using FluentAssertions;
using Xunit;

namespace EmailAgent.IntegrationTests.Plugins;

// ---------------------------------------------------------------------------
// M2T3 (task Risks "Default plugin auto-approval" / "Build-time SHA-256
// generation"): the hard-coded DefaultTrustAnchor is only safe because a test
// re-computes the SHA-256 of each connector DLL exactly as it sits in the test
// output directory and fails whenever the build-output bytes drift from the
// anchored values (then a developer updates DefaultTrustAnchor.cs). No DB and
// no host are needed — file reads and assembly metadata only, so this class
// deliberately stays OUT of the Postgres collection.
// ---------------------------------------------------------------------------

/// <summary>
/// Pins <see cref="DefaultTrustAnchor"/>: it covers exactly the three DEFAULT
/// connector plugins at v1.0.0, its SHA-256 values match the on-disk DLLs in
/// <c>AppContext.BaseDirectory</c>, and each anchored assembly's embedded
/// <c>plugin.json</c> declares the identity the anchor claims (plus passes the
/// load-time capability gate).
/// </summary>
[Trait("Category", "M2T3")]
public class DefaultTrustAnchorTests
{
    /// <summary>The three DEFAULT plugins of §5 / M2 SQ-4 — no fourth entry may exist.</summary>
    private static readonly string[] DefaultPluginIds =
        ["imap", "openai-compatible", "telegram-send"];

    [Fact]
    public void TrustAnchor_Covers_Exactly_The_Three_Connector_Plugins_At_V1_0_0()
    {
        var anchor = new DefaultTrustAnchor();

        anchor.Entries.Should().HaveCount(
            3, "only the three DEFAULT connector plugins may ever be pre-approved");
        anchor.Entries.Select(e => e.PluginId).Should().BeEquivalentTo(DefaultPluginIds);
        anchor.Entries.Select(e => e.AssemblyFileName).Should().OnlyHaveUniqueItems();
        anchor.Entries.Should().OnlyContain(
            e => e.Version == "1.0.0", "the anchored versions are exact (IsDefault matches all three parts)");
        anchor.Entries.Should().OnlyContain(
            e => !e.AssemblyFileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase),
            "AssemblyFileName is the file name WITHOUT extension (the loader appends .dll)");
        anchor.Entries.Should().OnlyContain(e => e.Sha256.Length == 32, "SHA-256 output is 32 bytes");

        var imap = anchor.Entries.Single(e => e.PluginId == "imap");
        anchor.IsDefault("imap", "1.0.0", imap.Sha256).Should().BeTrue();
        anchor.IsDefault("imap", "2.0.0", imap.Sha256).Should().BeFalse(
            "a different manifest version must not inherit the anchored bytes");
        anchor.IsDefault("not-a-default-plugin", "1.0.0", imap.Sha256).Should().BeFalse(
            "a third-party plugin_id can never match the anchor (M2T3 Risks: Default plugin auto-approval)");

        var tampered = (byte[])imap.Sha256.Clone();
        tampered[0] ^= 0xFF;
        anchor.IsDefault("imap", "1.0.0", tampered).Should().BeFalse(
            "one flipped byte falls through to the normal signature/approval gates");
    }

    /// <summary>
    /// The core anti-drift test of the task: re-compute SHA-256 from the DLL
    /// in the test output directory and assert it equals the hard-coded
    /// DefaultTrustAnchor value for every entry.
    /// </summary>
    [Fact]
    public void TrustAnchor_Sha256_Matches_The_On_Disk_Connector_Dlls()
    {
        var anchor = new DefaultTrustAnchor();

        foreach (var entry in anchor.Entries)
        {
            var path = Path.Combine(AppContext.BaseDirectory, entry.AssemblyFileName + ".dll");
            File.Exists(path).Should().BeTrue(
                $"{entry.PluginId} must reach the test output transitively via the EmailAgent.Api project reference (§5); looked for {path}");

            var recomputed = SHA256.HashData(File.ReadAllBytes(path));
            Convert.ToHexString(entry.Sha256).Should().Be(
                Convert.ToHexString(recomputed),
                $"the anchored SHA-256 for {entry.PluginId} {entry.Version} must equal the bytes on disk — "
                + "if this fails, DefaultTrustAnchor.cs was not updated after a connector rebuild");

            anchor.IsDefault(entry.PluginId, entry.Version, recomputed).Should().BeTrue(
                "the loader pre-approves build-output bytes only when all three parts match");
        }
    }

    /// <summary>
    /// The anchored identity must agree with what each assembly actually
    /// carries embedded (the loader reads that resource for the DEFAULT path
    /// and re-checks plugin_id/version/assembly before consulting the anchor),
    /// and the shipped manifest must pass the load-time capability gate.
    /// </summary>
    [Fact]
    public void Embedded_Plugin_Json_Matches_The_Trust_Anchor_Entry()
    {
        var anchor = new DefaultTrustAnchor();
        var validator = new PluginManifestValidator();

        foreach (var entry in anchor.Entries)
        {
            var path = Path.Combine(AppContext.BaseDirectory, entry.AssemblyFileName + ".dll");
            var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(path);

            // <AssemblyFileName>.plugin.json — the resource-name convention
            // PluginLoader.ReadEmbeddedManifest probes (suffix match).
            var resourceName = entry.AssemblyFileName + ".plugin.json";
            assembly.GetManifestResourceNames().Should().Contain(
                resourceName, "each DEFAULT connector embeds its plugin.json under its assembly name");

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"{resourceName} disappeared from {path}");
            using var reader = new StreamReader(stream);
            using var doc = JsonDocument.Parse(reader.ReadToEnd());
            var manifest = PluginManifest.FromJsonElement(doc.RootElement);

            manifest.PluginId.Should().Be(entry.PluginId);
            manifest.Version.Should().Be(entry.Version);
            manifest.AssemblyName.Should().Be(
                entry.AssemblyFileName + ".dll", "the DEFAULT path rejects a mismatching assembly name");

            validator.Validate(manifest).Ok.Should().BeTrue(
                $"the shipped {entry.PluginId} manifest must pass PluginManifestValidator at load time");
        }
    }
}
