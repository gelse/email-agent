using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmailAgent.Core.Audit;
using EmailAgent.Core.Options;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using EmailAgent.Infrastructure.Plugins;
using EmailAgent.IntegrationTests.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace EmailAgent.IntegrationTests.Plugins;

// ---------------------------------------------------------------------------
// M2T3 (task Scope §In + Acceptance criteria): the loader's gates exercised
// end to end against Testcontainers Postgres + the real host. Every test
// plants candidates in its OWN temp drop folder BEFORE the host starts, so
// the startup scan (PluginLoader.StartAsync) is what the assertions observe —
// no production endpoints are called (AGENTS.md DoD). The fabricated drop-in
// reuses the shipped IMAP connector's bytes paired with a manifest-declared
// entry type, which is exactly what lets it pass instantiation while
// carrying a plugin_id outside DefaultTrustAnchor (the "fabricated 4th
// plugin" of acceptance criterion 4).
// ---------------------------------------------------------------------------

/// <summary>
/// Integration coverage for the M2T3 <see cref="PluginLoader"/>: signed
/// acceptance, unsigned/tampered/unapproved/capability rejections (each with
/// its <c>plugin_rejected</c> audit reason), DEFAULT plugins loading from the
/// build output via <see cref="DefaultTrustAnchor"/>, and the Linux-only
/// SIGHUP reload (WeakReference + GC spin-wait per the task's Risks).
/// </summary>
[Trait("Category", "M2T3")]
[Collection("Postgres")]
public class PluginLoaderTests
{
    private const string AcceptedPluginId = "m2t3-accepted";
    private const string UnsignedPluginId = "m2t3-unsigned";
    private const string TamperedPluginId = "m2t3-tampered";
    private const string ManifestTamperedPluginId = "m2t3-manifest-tampered";
    private const string BadEntryTypePluginId = "m2t3-bad-entry-type";
    private const string UnapprovedPluginId = "m2t3-fabricated-4th";
    private const string EgressEmptyPluginId = "m2t3-egress-empty";
    private const string SighupPluginId = "m2t3-sighup";

    /// <summary>Drop-in file name every test plants (companion = &lt;base&gt;.plugin.json).</summary>
    private const string CompanionDll = "Fabricated.Plugin.dll";

    private static readonly string[] DefaultPluginIds =
        { "imap", "openai-compatible", "telegram-send" };

    /// <summary>
    /// M3T1: each plugin occupies exactly ONE registry slot — since the
    /// OpenAI-compatible entry type is an <c>IAgentEngine</c>, only these two
    /// DEFAULT plugins appear in <see cref="IPluginRegistry.MessageSourceIds"/>.
    /// <see cref="DefaultPluginIds"/> still names all three for identity /
    /// signature / approval assertions.
    /// </summary>
    private static readonly string[] DefaultMessageSourceIds = { "imap", "telegram-send" };

    /// <summary>M3T1: the DEFAULT plugins occupying the IAgentEngine slot.</summary>
    private static readonly string[] DefaultAgentEngineIds = { "openai-compatible" };

    private readonly PostgresFixture _postgres;

    public PluginLoaderTests(PostgresFixture postgres) => _postgres = postgres;

    // -----------------------------------------------------------------------
    // Acceptance criteria 1 + 8 + 9: a correctly signed AND approved drop-in
    // registers, its SQ-4 plugin_signatures row is persisted, the DEFAULT
    // plugins ride along, and the audit HMAC chain stays valid through the
    // plugin_loaded rows (tools/audit-verify gate).
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Loader_Accepts_Signed_Plugin_And_Registers_It_In_Registry()
    {
        var connectionString = await AccountTestHost.MigrateAsync(_postgres);
        var signingKey = RandomSigningKey();
        var directory = CreatePluginsDirectory();
        var bytes = ImapAssemblyBytes();

        PlantPlugin(directory, AcceptedPluginId, "1.0.0", bytes, signingKey);
        await ApproveAsync(connectionString, AcceptedPluginId, "1.0.0");

        await using var factory = ApiTestFactory.Create(connectionString, directory, signingKey);
        var client = factory.CreateClient(); // host start — the startup scan completes here

        var registry = factory.Services.GetRequiredService<IPluginRegistry>();
        registry.GetMessageSource(AcceptedPluginId).Should().NotBeNull(
            "a plugin signed with Plugin:SigningKey and present in plugin_approvals must register");
        registry.MessageSourceIds.Should().BeEquivalentTo(
            new[] { "imap", "telegram-send", AcceptedPluginId },
            "the two DEFAULT message sources load alongside the accepted drop-in");
        registry.AgentEngineIds.Should().BeEquivalentTo(
            DefaultAgentEngineIds, "the DEFAULT OpenAI-compatible engine occupies the engine slot");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();

        var signature = await db.PluginSignatures.AsNoTracking()
            .SingleOrDefaultAsync(s => s.PluginId == AcceptedPluginId && s.Version == "1.0.0");
        signature.Should().NotBeNull(
            "the loader must persist the SQ-4 plugin_signatures row for what it verified");
        signature!.AssemblySha256.Should().Equal(SHA256.HashData(bytes));
        signature.Signature.Should().NotBeEmpty();

        var loaded = await FindAuditMetadataAsync(
            factory, AuditActions.PluginLoaded, pluginId: AcceptedPluginId);
        loaded.Should().NotBeNull("plugin_loaded must be audited for the accepted drop-in");

        // M2T3 nested review round 1/2: the GRANTED scope is part of the
        // audited record — plugin_loaded carries the capabilities and
        // http_allowlist the gates evaluated, in the row itself.
        var loadedDoc = ParseMetadata(loaded!);
        loadedDoc.GetProperty("plugin_id").GetString().Should().Be(AcceptedPluginId);
        loadedDoc.GetProperty("capabilities").GetArrayLength().Should().Be(1);
        loadedDoc.GetProperty("capabilities")[0].GetString().Should().Be("processed_messages_write");
        loadedDoc.GetProperty("http_allowlist").GetArrayLength().Should().Be(0);

        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
        (await audit.VerifyChainAsync(CancellationToken.None)).Should().BeNull(
            "plugin load events must keep the M1T4 HMAC chain valid (tools/audit-verify)");
    }

    // -----------------------------------------------------------------------
    // Acceptance criteria 2–4: every refusal path leaves the plugin out of
    // the registry AND writes a plugin_rejected audit row with a clear,
    // machine-readable reason.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Loader_Rejects_Unsigned_Plugin_With_Clear_Error()
    {
        var connectionString = await AccountTestHost.MigrateAsync(_postgres);
        var signingKey = RandomSigningKey();
        var directory = CreatePluginsDirectory();
        var bytes = ImapAssemblyBytes();

        // No signature field at all — the first gate after manifest parse.
        PlantPlugin(directory, UnsignedPluginId, "1.0.0", bytes, signingKey: null);

        await using var factory = ApiTestFactory.Create(connectionString, directory, signingKey);
        var client = factory.CreateClient();

        var registry = factory.Services.GetRequiredService<IPluginRegistry>();
        registry.GetMessageSource(UnsignedPluginId).Should().BeNull();
        registry.MessageSourceIds.Should().BeEquivalentTo(DefaultMessageSourceIds);

        var rejection = await FindAuditMetadataAsync(
            factory, AuditActions.PluginRejected,
            reason: PluginSignatureException.Unsigned,
            assemblyFile: CompanionDll);
        rejection.Should().NotBeNull("an unsigned candidate must be audited as plugin_rejected/unsigned");
        var doc = ParseMetadata(rejection!);
        doc.GetProperty("detail").GetString().Should().Be("signature");
        // PluginSignatureException's message names the companion manifest the
        // loader found (no signature was in it), not the manifest's plugin_id.
        doc.GetProperty("message").GetString()!
            .Should().Contain("no signature in " + Path.GetFileNameWithoutExtension(CompanionDll) + ".plugin.json");
    }

    [Fact]
    public async Task Loader_Rejects_Tampered_Plugin_With_Clear_Error()
    {
        var connectionString = await AccountTestHost.MigrateAsync(_postgres);
        var signingKey = RandomSigningKey();
        var directory = CreatePluginsDirectory();
        var bytes = ImapAssemblyBytes();

        // Signature computed over the pristine bytes + manifest; the planted
        // DLL differs by one flipped byte — PluginSignatureVerifier.Verify must fail.
        var tampered = (byte[])bytes.Clone();
        tampered[^1] ^= 0xFF;
        PlantPlugin(directory, TamperedPluginId, "1.0.0", tampered, signingKey, bytesToSign: bytes);

        await using var factory = ApiTestFactory.Create(connectionString, directory, signingKey);
        var client = factory.CreateClient();

        var registry = factory.Services.GetRequiredService<IPluginRegistry>();
        registry.GetMessageSource(TamperedPluginId).Should().BeNull();
        registry.MessageSourceIds.Should().BeEquivalentTo(DefaultMessageSourceIds);

        var rejection = await FindAuditMetadataAsync(
            factory, AuditActions.PluginRejected,
            reason: PluginSignatureException.SignatureMismatch,
            assemblyFile: CompanionDll);
        rejection.Should().NotBeNull(
            "acceptance criterion 2: the tampered DLL must be refused with reason signature_mismatch");
        ParseMetadata(rejection!).GetProperty("detail").GetString().Should().Be("signature");
    }

    // -----------------------------------------------------------------------
    // M2T3 nested-review regression (WARNING 1): the companion manifest is
    // INSIDE the signed input, so editing capabilities / http_allowlist after
    // signing — the exact privilege-escalation path the review flagged — must
    // fail the signature gate before approval or capability checks ever run.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Loader_Rejects_Plugin_Whose_Companion_Manifest_Tampered_After_Signing()
    {
        var connectionString = await AccountTestHost.MigrateAsync(_postgres);
        var signingKey = RandomSigningKey();
        var directory = CreatePluginsDirectory();
        var bytes = ImapAssemblyBytes();

        // Sign the PRISTINE manifest, then elevate its grants while keeping
        // the original signature value in the file.
        var manifestText = BuildManifest(
            ManifestTamperedPluginId, "1.0.0", capabilities: null, httpAllowlist: null, typeName: null);
        var signature = Sign(bytes, manifestText, signingKey);

        var tampered = JsonNode.Parse(manifestText)?.AsObject()
            ?? throw new InvalidOperationException("test manifest is not a JSON object");
        tampered["capabilities"] = new JsonArray("processed_messages_write", "http_egress");
        tampered["http_allowlist"] = new JsonArray("evil.example.com");
        tampered["signature"] = signature;
        WriteDropIn(directory, bytes, tampered.ToJsonString());

        // Approval present so ONLY the signature gate can stop the candidate
        // (if the manifest were outside the HMAC, this plugin would load).
        await ApproveAsync(connectionString, ManifestTamperedPluginId, "1.0.0");

        await using var factory = ApiTestFactory.Create(connectionString, directory, signingKey);
        var client = factory.CreateClient();

        var registry = factory.Services.GetRequiredService<IPluginRegistry>();
        registry.GetMessageSource(ManifestTamperedPluginId).Should().BeNull(
            "a manifest edited after signing must not register — its fields are covered by the HMAC");
        registry.MessageSourceIds.Should().BeEquivalentTo(DefaultMessageSourceIds);

        // Filter by the message's unique plugin id: every signature_mismatch
        // row shares action + reason + assembly_file across this collection.
        var rejection = await FindAuditMetadataAsync(
            factory, AuditActions.PluginRejected,
            reason: PluginSignatureException.SignatureMismatch,
            assemblyFile: CompanionDll,
            messageContains: ManifestTamperedPluginId);
        rejection.Should().NotBeNull(
            "the post-signing manifest edit must be refused with reason signature_mismatch (review WARNING 1)");
        ParseMetadata(rejection!).GetProperty("detail").GetString().Should().Be("signature");
    }

    [Fact]
    public async Task Loader_Rejects_Plugin_Not_In_PluginApprovals_With_Clear_Error()
    {
        var connectionString = await AccountTestHost.MigrateAsync(_postgres);
        var signingKey = RandomSigningKey();
        var directory = CreatePluginsDirectory();
        var bytes = ImapAssemblyBytes();

        // Correctly signed — but the fabricated 4th plugin_id has no
        // plugin_approvals row and cannot match DefaultTrustAnchor (Q-O5:
        // approval is required for every non-DEFAULT plugin version).
        PlantPlugin(directory, UnapprovedPluginId, "9.9.9", bytes, signingKey);

        await using var factory = ApiTestFactory.Create(connectionString, directory, signingKey);
        var client = factory.CreateClient();

        var registry = factory.Services.GetRequiredService<IPluginRegistry>();
        registry.GetMessageSource(UnapprovedPluginId).Should().BeNull();
        registry.MessageSourceIds.Should().BeEquivalentTo(DefaultMessageSourceIds);

        var rejection = await FindAuditMetadataAsync(
            factory, AuditActions.PluginRejected,
            reason: "not_approved",
            assemblyFile: CompanionDll);
        rejection.Should().NotBeNull(
            "a signed but unapproved plugin must be refused with reason not_approved");
        var doc = ParseMetadata(rejection!);
        doc.GetProperty("message").GetString()!.Should().Contain("plugin_approvals");
    }

    [Fact]
    public async Task Loader_Rejects_Plugin_With_HttpEgress_And_Empty_Allowlist_With_Clear_Error()
    {
        var connectionString = await AccountTestHost.MigrateAsync(_postgres);
        var signingKey = RandomSigningKey();
        var directory = CreatePluginsDirectory();
        var bytes = ImapAssemblyBytes();

        // Signed AND approved so the candidate reaches the capability gate
        // (manifest validation at load time only — SQ-6 / Q-D6 / Q-D3).
        PlantPlugin(
            directory,
            EgressEmptyPluginId,
            "1.0.0",
            bytes,
            signingKey,
            capabilities: new[] { "http_egress" },
            httpAllowlist: Array.Empty<string>());
        await ApproveAsync(connectionString, EgressEmptyPluginId, "1.0.0");

        await using var factory = ApiTestFactory.Create(connectionString, directory, signingKey);
        var client = factory.CreateClient();

        var registry = factory.Services.GetRequiredService<IPluginRegistry>();
        registry.GetMessageSource(EgressEmptyPluginId).Should().BeNull(
            "HttpEgress with an empty http_allowlist must fail PluginManifestValidator and the loader must refuse it");

        // M2T3 nested review round 2 (INFO): the capability refusal carries
        // plugin_id in its metadata (PluginCapabilityException.PluginId is
        // threaded into the rejection row), so the row is found by action +
        // reason + plugin_id — no message-text pinning needed.
        var rejection = await FindAuditMetadataAsync(
            factory, AuditActions.PluginRejected,
            reason: "manifest_invalid",
            pluginId: EgressEmptyPluginId);
        rejection.Should().NotBeNull(
            "acceptance criterion 5: the capability-gate refusal must be audited");
        var doc = ParseMetadata(rejection!);
        doc.GetProperty("detail").GetString().Should().Be("capabilities");
        doc.GetProperty("plugin_id").GetString().Should().Be(EgressEmptyPluginId);
        doc.GetProperty("message").GetString()!
            .Should().Contain(EgressEmptyPluginId)
            .And.Contain("http_allowlist is empty");
    }

    // -----------------------------------------------------------------------
    // M2T3 nested-review regression (WARNING 3): instantiation uses ONLY the
    // manifest-declared entry type — no silent fallback to "whatever
    // IMessageSource happens to be instantiable first".
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Loader_Rejects_Plugin_Whose_Declared_Entry_Type_Is_Not_In_The_Assembly()
    {
        var connectionString = await AccountTestHost.MigrateAsync(_postgres);
        var signingKey = RandomSigningKey();
        var directory = CreatePluginsDirectory();
        var bytes = ImapAssemblyBytes();

        // Signed AND approved — the candidate must reach instantiation and be
        // refused there: the assembly contains IMessageSource implementations,
        // but none of them is the declared type.
        PlantPlugin(
            directory, BadEntryTypePluginId, "1.0.0", bytes, signingKey,
            typeName: "M2T3.NoSuchEntryType");
        await ApproveAsync(connectionString, BadEntryTypePluginId, "1.0.0");

        await using var factory = ApiTestFactory.Create(connectionString, directory, signingKey);
        var client = factory.CreateClient();

        var registry = factory.Services.GetRequiredService<IPluginRegistry>();
        registry.GetMessageSource(BadEntryTypePluginId).Should().BeNull(
            "a manifest declaring a type the assembly does not contain must not fall back to another IMessageSource");
        registry.MessageSourceIds.Should().BeEquivalentTo(DefaultMessageSourceIds);

        var rejection = await FindAuditMetadataAsync(
            factory, AuditActions.PluginRejected,
            reason: "load_failed",
            pluginId: BadEntryTypePluginId);
        rejection.Should().NotBeNull(
            "the undeclared entry type must be refused with reason load_failed (review WARNING 3)");
        var doc = ParseMetadata(rejection!);
        doc.GetProperty("detail").GetString().Should().Be("no_instantiable_message_source");
        doc.GetProperty("message").GetString()!.Should().Contain("M2T3.NoSuchEntryType");
    }

    // -----------------------------------------------------------------------
    // M2T3 nested review round 2 (WARNING): a companion-less DLL in the drop
    // folder is a sibling dependency of the documented PluginLoadContext
    // probing workflow — never a plugin candidate. It must be skipped BEFORE
    // mapping (its module initializers must not run pre-gate) and must leave
    // NO audit row: no plugin_rejected for a refusal that never happened and
    // no plugin_loaded for an assembly that never loaded.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Loader_Skips_Companionless_Dependency_Dll_Without_Any_Audit_Row()
    {
        var connectionString = await AccountTestHost.MigrateAsync(_postgres);
        var signingKey = RandomSigningKey();
        var directory = CreatePluginsDirectory();
        var bytes = ImapAssemblyBytes();

        // The real candidate: signed, approved, with its companion manifest.
        const string hostPluginId = "m2t3-dependency-host";
        PlantPlugin(directory, hostPluginId, "1.0.0", bytes, signingKey);
        await ApproveAsync(connectionString, hostPluginId, "1.0.0");

        // The sibling dependency: plain DLL bytes, no <base>.plugin.json
        // companion, file name that is NOT a DefaultTrustAnchor entry.
        const string dependencyDll = "Fabricated.Dependency.dll";
        File.WriteAllBytes(Path.Combine(directory, dependencyDll), bytes);

        await using var factory = ApiTestFactory.Create(connectionString, directory, signingKey);
        var client = factory.CreateClient(); // host start — the startup scan sees both files

        var registry = factory.Services.GetRequiredService<IPluginRegistry>();
        registry.GetMessageSource(hostPluginId).Should().NotBeNull(
            "the signed and approved plugin must register even with a sibling dependency beside it");
        registry.MessageSourceIds.Should().BeEquivalentTo(
            new[] { "imap", "telegram-send", hostPluginId },
            "the dependency must not become a registry entry of its own");

        var rejectionRow = await FindAuditMetadataAsync(
            factory, AuditActions.PluginRejected,
            assemblyFile: dependencyDll);
        rejectionRow.Should().BeNull(
            "a companion-less non-anchor DLL is skipped before any gate — no rejection row may exist for it");

        var loadedRow = await FindAuditMetadataAsync(
            factory, AuditActions.PluginLoaded,
            assemblyFile: dependencyDll);
        loadedRow.Should().BeNull(
            "the dependency is never mapped, so no plugin_loaded row may reference its file name");
    }

    // -----------------------------------------------------------------------
    // Acceptance criterion 6: the three DEFAULT plugins load from the build
    // output with NO companion file on disk (their plugin.json is embedded),
    // WITHOUT a plugin_approvals row — DefaultTrustAnchor is what pre-approves
    // them. DefaultTrustAnchorTests pins the anchored bytes themselves.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Loader_Loads_Default_Plugins_From_Referenced_Assemblies_Without_Signature_On_Disk()
    {
        var connectionString = await AccountTestHost.MigrateAsync(_postgres);

        // No plugins directory planted: only the build-output DEFAULT path runs.
        await using var factory = ApiTestFactory.Create(connectionString);
        var client = factory.CreateClient();

        var registry = factory.Services.GetRequiredService<IPluginRegistry>();
        registry.MessageSourceIds.Should().BeEquivalentTo(DefaultMessageSourceIds);
        registry.AgentEngineIds.Should().BeEquivalentTo(
            DefaultAgentEngineIds, "M3T1: the OpenAI-compatible DEFAULT plugin occupies the engine slot");
        registry.GetAgentEngine("openai-compatible").Should().NotBeNull(
            "the engine entry type registered through the loader's IAgentEngine dispatch");
        registry.GetMessageSource("openai-compatible").Should().BeNull(
            "each plugin occupies exactly ONE slot — the engine class is not an IMessageSource");

        foreach (var assemblyName in new[]
                     { "EmailAgent.Connectors.Imap", "EmailAgent.Connectors.OpenAI", "EmailAgent.Connectors.Telegram" })
        {
            File.Exists(Path.Combine(AppContext.BaseDirectory, assemblyName + ".plugin.json"))
                .Should().BeFalse(
                    "DEFAULT plugins ship plugin.json as an EMBEDDED resource, never as a companion file on disk");
        }

        var imapPath = Path.Combine(AppContext.BaseDirectory, "EmailAgent.Connectors.Imap.dll");
        File.Exists(imapPath).Should().BeTrue(
            "the IMAP connector must ride along into the test output via the EmailAgent.Api project reference (§5)");
        var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(imapPath);
        assembly.GetManifestResourceNames()
            .Should().Contain("EmailAgent.Connectors.Imap.plugin.json");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
        var approvals = await db.PluginApprovals.AsNoTracking()
            .Where(a => DefaultPluginIds.Contains(a.PluginId))
            .ToListAsync();
        approvals.Should().BeEmpty(
            "the DEFAULT plugins must load via DefaultTrustAnchor without any plugin_approvals row");

        // M2T3 nested review round 2 (INFO): anchored loads still persist the
        // SQ-4 evidence row — the full 32-byte SHA-256 of the anchored bytes
        // and an EMPTY signature, because Q-O5 anchor trust waives the HMAC
        // and the loader must never fabricate a signature it did not verify.
        var signatures = await db.PluginSignatures.AsNoTracking()
            .Where(s => DefaultPluginIds.Contains(s.PluginId))
            .ToListAsync();
        signatures.Should().HaveCount(
            3,
            "exactly one (plugin_id, version) row per DEFAULT plugin — ux_plugin_signatures keeps it unique");
        signatures.Select(s => s.PluginId).Should().BeEquivalentTo(DefaultPluginIds);
        foreach (var row in signatures)
        {
            row.AssemblySha256.Should().HaveCount(32, "SHA-256 is 32 raw bytes");
            row.Signature.Should().BeEmpty(
                "anchor-trust loads verify no HMAC (Q-O5) and must record an empty signature, not a made-up one");
        }
    }

    // -----------------------------------------------------------------------
    // M3T1 review WARNING: the engine transport is the host's NAMED HttpClient
    // and must carry a generous but FINITE timeout — an unbounded call can
    // wedge a caller forever behind a stalled provider, while the framework's
    // 100 s default aborts slow generations. If Program.cs ever drops the
    // ConfigureHttpClient bound, this test fails before the defect ships.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Engine_Transport_HttpClient_Carries_The_Finite_Engine_Timeout()
    {
        var connectionString = await AccountTestHost.MigrateAsync(_postgres);
        await using var factory = ApiTestFactory.Create(connectionString);
        _ = factory.CreateClient(); // host start

        var httpClient = factory.Services.GetRequiredService<HttpClient>();

        httpClient.Timeout.Should().Be(
            TimeSpan.FromMinutes(30),
            "the DI-resolved engine client must keep the configured bound — neither infinite nor the 100 s default");
    }

    // -----------------------------------------------------------------------
    // Acceptance criterion 7 (Linux): SIGHUP reloads the drop folder — the
    // old collectible AssemblyLoadContext is discarded (WeakReference + GC
    // spin-wait, per the task's Risks) and exactly one entry per plugin_id
    // remains in the registry.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SIGHUP_Reload_Discards_Old_AssemblyLoadContext_And_Registers_Updated_Plugin()
    {
        if (!OperatingSystem.IsLinux())
        {
            // Program.cs installs no SIGHUP handler on Windows — reloads happen
            // on restart there (M10), so the signal-based assertions are Linux-only.
            return;
        }

        var connectionString = await AccountTestHost.MigrateAsync(_postgres);
        var signingKey = RandomSigningKey();
        var directory = CreatePluginsDirectory();
        var bytes = ImapAssemblyBytes();

        PlantPlugin(directory, SighupPluginId, "1.0.0", bytes, signingKey);
        await ApproveAsync(connectionString, SighupPluginId, "1.0.0");

        await using var factory = ApiTestFactory.Create(connectionString, directory, signingKey);
        var client = factory.CreateClient();

        var registry = factory.Services.GetRequiredService<IPluginRegistry>();
        var loader = factory.Services.GetRequiredService<PluginLoader>();
        registry.GetMessageSource(SighupPluginId).Should().NotBeNull(
            "the drop-in must be registered before the reload");

        // The local returned here goes out of scope immediately: after this
        // point only the loader's current generation keeps the old context
        // reachable, so its death after SIGHUP proves the unload.
        var oldContext = CaptureDropInContextWeak(loader);

        // Backup handler: guarantees THIS test process survives the signal even
        // if the host's own registration were ever gone (a default SIGHUP would
        // terminate it). Program.cs's handler — installed when this factory's
        // host started — still performs the actual reload; both callbacks run.
        using var safety = PosixSignalRegistration.Create(
            PosixSignal.SIGHUP, context => context.Cancel = true);

        SendSighupToThisProcess();

        // Unload is asynchronous (UnloadAsync polls GC.Collect() for up to 5s
        // inside the loader); spin here too so a slow collection cannot flake.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (oldContext.IsAlive && DateTime.UtcNow < deadline)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(100);
        }

        oldContext.IsAlive.Should().BeFalse(
            "the SIGHUP reload must discard the previous drop-in AssemblyLoadContext");

        // If the old context is dead, any snapshot entry for the drop-in can
        // only be the NEW generation (an entry holds a strong context ref).
        var snapshot = loader.SnapshotLoaded();
        snapshot.Count(p => p.FileName == CompanionDll).Should().Be(1);
        snapshot.GroupBy(p => p.Manifest.PluginId)
            .Should().OnlyContain(
                group => group.Count() == 1,
                "the reload must leave exactly one entry per plugin_id");
        registry.MessageSourceIds.Should().BeEquivalentTo(
            new[] { "imap", "telegram-send", SighupPluginId });
    }

    // -----------------------------------------------------------------------
    // M2T3 review-gate regression: StopAsync must DRAIN the current
    // generation (unregister + unload) without re-running the load cycle.
    // The pre-fix drain called SwapAsync with its unconditional rescan, so
    // every host stop emitted a second plugin_loaded audit row per DEFAULT
    // plugin (root-caused by the nested smoke gate's F6 row check).
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Shutdown_Drains_Without_Writing_Plugin_Loaded_Audit_Rows()
    {
        var connectionString = await AccountTestHost.MigrateAsync(_postgres);

        long afterBoot;
        await using (var factory = ApiTestFactory.Create(connectionString))
        {
            var client = factory.CreateClient(); // host start — the startup scan audits plugin_loaded ×3
            afterBoot = await CountAuditAsync(connectionString, AuditActions.PluginLoaded);
            afterBoot.Should().BeGreaterThanOrEqualTo(
                3, "the three DEFAULT plugins must have loaded and been audited");
        } // dispose → hosted-service StopAsync — the drain under test

        var afterShutdown = await CountAuditAsync(connectionString, AuditActions.PluginLoaded);
        afterShutdown.Should().Be(
            afterBoot,
            "shutdown may only unload: StopAsync must not re-scan/re-load and thus never add plugin_loaded rows");
    }

    // -----------------------------------------------------------------------
    // M2T7 reconciliation (recorded as an M2T3 deviation — refusal-wins): a
    // refused drop-in may CLAIM the plugin_id of a good DEFAULT plugin; the
    // audit then says "refused" while the registry still resolves the id, so
    // test-connection would keep succeeding against the plugin the operator
    // just tampered with (smoke flow 3 of M2T7). The loader must unregister
    // the claimed id after the scan, and the refusal row must carry the
    // plugin_id that keys that unregister.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Loader_Unregisters_Default_Plugin_Whose_Id_A_Refused_DropIn_Claims()
    {
        var connectionString = await AccountTestHost.MigrateAsync(_postgres);
        var signingKey = RandomSigningKey();
        var directory = CreatePluginsDirectory();
        var bytes = ImapAssemblyBytes();

        // Tampered bytes paired with a companion manifest claiming the DEFAULT
        // plugin_id "imap" — exactly the M2 smoke flow 3 artifact: the
        // signature gate refuses the candidate at the startup scan.
        var tampered = (byte[])bytes.Clone();
        tampered[^1] ^= 0xFF;
        PlantPlugin(directory, "imap", "9.9.9", tampered, signingKey, bytesToSign: bytes);

        await using var factory = ApiTestFactory.Create(connectionString, directory, signingKey);
        var client = factory.CreateClient(); // host start — the startup scan runs the refusal

        var registry = factory.Services.GetRequiredService<IPluginRegistry>();
        registry.GetMessageSource("imap").Should().BeNull(
            "refusal-wins: the refused drop-in claimed plugin_id imap, so the good build-output imap must not survive the scan");
        registry.MessageSourceIds.Should().BeEquivalentTo(
            new[] { "telegram-send" },
            "only the DEFAULT message source whose id was not poisoned by the refusal remains registered");
        registry.AgentEngineIds.Should().BeEquivalentTo(
            DefaultAgentEngineIds,
            "the refusal claimed plugin_id imap — the engine slot is untouched by that unregister");

        // The signature_mismatch row must carry the claimed plugin_id — that
        // is the value the refusal-wins unregister keys on, and what lets an
        // operator correlate the refusal with the id that went offline.
        var rejection = await FindAuditMetadataAsync(
            factory, AuditActions.PluginRejected,
            reason: PluginSignatureException.SignatureMismatch,
            assemblyFile: CompanionDll,
            pluginId: "imap");
        rejection.Should().NotBeNull(
            "the signature_mismatch audit row must carry plugin_id=imap (threaded from the companion manifest)");
        ParseMetadata(rejection!).GetProperty("detail").GetString().Should().Be("signature");
    }

    // -----------------------------------------------------------------------
    // Helpers.
    // -----------------------------------------------------------------------

    /// <summary>Per-test drop folder so scans in one test can never see another test's candidates.</summary>
    private static string CreatePluginsDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "email-agent-tests",
            "m2t3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Random per-test signing key (hermetic; >= 32 bytes after base64).</summary>
    private static string RandomSigningKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

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
    /// Builds the companion manifest WITHOUT a <c>signature</c> member — the
    /// exact text the HMAC covers (the verifier canonicalizes it by removing
    /// <c>signature</c>, which is not present yet at signing time).
    /// </summary>
    private static string BuildManifest(
        string pluginId,
        string version,
        IReadOnlyList<string>? capabilities,
        IReadOnlyList<string>? httpAllowlist,
        string? typeName)
    {
        var manifest = new Dictionary<string, object?>
        {
            ["plugin_id"] = pluginId,
            ["version"] = version,
            ["assembly"] = CompanionDll,
            ["type"] = typeName ?? "EmailAgent.Connectors.Imap.ImapMessageSource",
            ["capabilities"] = capabilities ?? new[] { "processed_messages_write" },
            ["http_allowlist"] = httpAllowlist ?? Array.Empty<string>(),
            ["notes"] = "M2T3 integration-test fabricated drop-in (not a shipped plugin).",
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

    /// <summary>Writes the drop-in DLL plus its companion <c>Fabricated.Plugin.plugin.json</c>.</summary>
    private static void WriteDropIn(string directory, byte[] assemblyBytes, string companionText)
    {
        File.WriteAllBytes(Path.Combine(directory, CompanionDll), assemblyBytes);
        File.WriteAllText(
            Path.Combine(directory, Path.GetFileNameWithoutExtension(CompanionDll) + ".plugin.json"),
            companionText);
    }

    /// <summary>
    /// Plants the fabricated drop-in: DLL bytes plus the companion manifest
    /// the loader looks for next to it (naming convention:
    /// <c>&lt;assembly-base&gt;.plugin.json</c>). When <paramref name="signingKey"/>
    /// is non-null the manifest is signed (over <paramref name="bytesToSign"/>
    /// when the planted bytes themselves are the tampered artifact).
    /// </summary>
    private static void PlantPlugin(
        string directory,
        string pluginId,
        string version,
        byte[] assemblyBytes,
        string? signingKey,
        byte[]? bytesToSign = null,
        IReadOnlyList<string>? capabilities = null,
        IReadOnlyList<string>? httpAllowlist = null,
        string? typeName = null)
    {
        var manifestText = BuildManifest(pluginId, version, capabilities, httpAllowlist, typeName);
        var companionText = signingKey is null
            ? manifestText
            : WithSignature(manifestText, Sign(bytesToSign ?? assemblyBytes, manifestText, signingKey));
        WriteDropIn(directory, assemblyBytes, companionText);
    }

    /// <summary>
    /// Seeds a (plugin_id, version) row in <c>plugin_approvals</c> BEFORE the
    /// host starts — the loader reads approvals during its startup scan. The
    /// FK to users(id) requires a user row, seeded directly (no endpoint is
    /// called; AGENTS.md DoD).
    /// </summary>
    private static async Task ApproveAsync(string connectionString, string pluginId, string version)
    {
        var options = new DbContextOptionsBuilder<EmailAgentDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using var db = new EmailAgentDbContext(options);
        var email = $"m2t3-approver-{Guid.NewGuid():N}@example.test";
        var approver = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserName = email,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(),
            PasswordHash = "m2t3-seeded-not-a-real-login-hash",
            Role = UserRole.User,
            Status = UserStatus.Active,
            MustChangePassword = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.Users.Add(approver);
        db.PluginApprovals.Add(new PluginApproval
        {
            PluginId = pluginId,
            Version = version,
            ApprovedByUserId = approver.Id,
            ApprovedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// First <c>metadata_json</c> of an <c>audit_log</c> row matching the
    /// action and (optionally) the machine-readable reason / assembly file /
    /// plugin_id / message fragment — the shared container accumulates rows
    /// across tests, so every rejection assertion filters explicitly.
    /// </summary>
    private static async Task<string?> FindAuditMetadataAsync(
        WebApplicationFactory<Program> factory,
        string action,
        string? reason = null,
        string? assemblyFile = null,
        string? pluginId = null,
        string? messageContains = null)
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
            if (reason is not null
                && (!metadata.TryGetProperty("reason", out var reasonElement)
                    || !string.Equals(reasonElement.GetString(), reason, StringComparison.Ordinal)))
            {
                continue;
            }

            if (assemblyFile is not null
                && (!metadata.TryGetProperty("assembly_file", out var fileElement)
                    || !string.Equals(fileElement.GetString(), assemblyFile, StringComparison.Ordinal)))
            {
                continue;
            }

            if (pluginId is not null
                && (!metadata.TryGetProperty("plugin_id", out var pluginIdElement)
                    || !string.Equals(pluginIdElement.GetString(), pluginId, StringComparison.Ordinal)))
            {
                continue;
            }

            if (messageContains is not null)
            {
                var message = metadata.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : null;
                if (message is null || !message.Contains(messageContains, StringComparison.Ordinal))
                {
                    continue;
                }
            }

            return json;
        }

        return null;
    }

    private static JsonElement ParseMetadata(string metadataJson)
    {
        using var doc = JsonDocument.Parse(metadataJson);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Rows of one audit action, counted over the shared container through an
    /// OWN connection (factory services are gone after dispose — the shutdown
    /// regression test counts past the factory's end of life).
    /// </summary>
    private static async Task<long> CountAuditAsync(string connectionString, string action)
    {
        var options = new DbContextOptionsBuilder<EmailAgentDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using var db = new EmailAgentDbContext(options);
        return await db.AuditLog.AsNoTracking().LongCountAsync(row => row.Action == action);
    }

    /// <summary>
    /// Weak handle on the drop-in's current AssemblyLoadContext. The local
    /// <c>LoadedPlugin</c> record returned by the snapshot goes out of scope
    /// with this method — the test itself must hold no strong reference.
    /// </summary>
    private static WeakReference CaptureDropInContextWeak(PluginLoader loader)
    {
        var dropIn = loader.SnapshotLoaded().Single(p => p.FileName == CompanionDll);
        return new WeakReference(dropIn.Context);
    }

    /// <summary>
    /// Delivers a real SIGHUP to this test process via <c>kill -s HUP</c>
    /// (Linux-only test; the /bin/sh builtin path avoids P/Invoke analyzers).
    /// </summary>
    private static void SendSighupToThisProcess()
    {
        using var kill = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/sh",
            ArgumentList = { "-c", $"kill -s HUP {Environment.ProcessId}" },
        }) ?? throw new InvalidOperationException("could not start /bin/sh to deliver SIGHUP");

        kill.WaitForExit(10_000).Should().BeTrue("the kill helper must exit promptly");
        kill.ExitCode.Should().Be(
            0,
            "kill -s HUP must reach this process (the safety + Program.cs handlers suppress the default action)");
    }
}
