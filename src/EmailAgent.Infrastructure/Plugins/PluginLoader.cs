using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using EmailAgent.Contracts.AgentEngines;
using EmailAgent.Contracts.MessageSources;
using EmailAgent.Contracts.Plugins;
using EmailAgent.Core.Audit;
using EmailAgent.Core.Options;
using EmailAgent.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmailAgent.Infrastructure.Plugins;

/// <summary>
/// M2T3 (§9 / §4 / §5 / §17): the single gate through which every plugin
/// enters the process. A hosted service — <see cref="StartAsync"/> scans the
/// drop folder once at host start; on Linux a SIGHUP handler (installed by
/// Program.cs) triggers <see cref="Reload"/>; <see cref="StopAsync"/> unloads
/// collectible contexts gracefully (up to 5 s each, warning on timeout).
/// </summary>
/// <remarks>
/// <para>
/// Per-candidate gates, in order (fail-closed; a bad plugin is skipped and
/// audited as <c>plugin_rejected</c> — the loader never crashes the host):
/// <list type="number">
/// <item>manifest parse — companion file <c>&lt;assembly-name&gt;.plugin.json</c>
/// next to the DLL (e.g. <c>EmailAgent.Connectors.Imap.plugin.json</c>); an
/// embedded <c>&lt;assembly&gt;.plugin.json</c> resource is the fallback for
/// DEFAULT-named assemblies, which ship no companion file. A companion-less
/// DLL whose file name matches no <see cref="DefaultTrustAnchor"/> entry is
/// NOT a candidate: it is skipped at debug level without being mapped (it is
/// a sibling dependency — see below — and must never produce a
/// <c>plugin_rejected</c> row);</item>
/// <item>signature — <c>HMACSHA256(Plugin:SigningKey, SHA256(dll_bytes) ||
/// SHA256(manifest_bytes))</c> compared in fixed time; a disk companion is
/// ALWAYS signature-checked (the anchor never substitutes for it — review
/// fix), while an embedded-manifest candidate is only accepted when the
/// (plugin_id, version, sha256) triple matches <see cref="DefaultTrustAnchor"/>,
/// whose hash covers the embedded manifest itself;</item>
/// <item>approval — a <c>plugin_approvals</c> row for (plugin_id, version),
/// skipped for anchored DEFAULT plugins (Q-O5), including an anchored disk
/// companion (its manifest was just authenticated by the signature gate);
/// the lookup is fail-closed;</item>
/// <item>capability validation — <see cref="PluginManifestValidator"/> (M2
/// SQ-6 / Q-D6; enforced at load time only, Q-D3);</item>
/// <item>assembly load into a fresh collectible
/// <see cref="AssemblyLoadContext"/> — a companion candidate maps its bytes
/// only AFTER every gate above passed, and an embedded-manifest candidate
/// only after its file name matched the anchor AND its SHA-256 equals the
/// anchored hash (likewise the DEFAULT build-output path, hashed before it
/// is mapped);</item>
/// <item>instantiate + register in <see cref="IPluginRegistry"/> (keyed by
/// manifest <c>plugin_id</c>);</item>
/// <item>persist the <c>plugin_signatures</c> upsert and the
/// <c>plugin_loaded</c> audit event — both best-effort so DB-less or
/// not-yet-migrated hosts still boot (failures are logged, never thrown).</item>
/// </list>
/// </para>
/// <para>
/// Reload is "load the new generation, then unload the old": the registry is
/// cleared and repopulated (DEFAULTs first, drop-ins second so a drop-in can
/// override a same-id build-output plugin), the previous generation is
/// swapped out, and only then are its collectible contexts unloaded — a
/// plugin whose file disappeared is gone for good after the cycle. Managed
/// dependencies resolve from the default context (<see cref="PluginLoadContext"/>
/// overrides <see cref="AssemblyLoadContext.Load(AssemblyName)"/> to return
/// null) so <c>EmailAgent.Contracts</c> type identity is shared with the host;
/// anything else is probed in the plugin directory via <see cref="AssemblyLoadContext.Resolving"/>,
/// unmanaged libraries likewise via <see cref="AssemblyLoadContext.LoadUnmanagedDll"/>.
/// Those sibling files are NOT signature-checked (SQ-4 signs the plugin's own
/// assembly + manifest only): the drop folder — the whole plugin PACKAGE of
/// signed plugin + sibling dependencies — is therefore an operator-only path.
/// The signature authenticates the plugin's identity and capability scope;
/// whoever can write the folder is trusted for its file contents, the same
/// privilege that places the signed DLL itself (M2T3 nested review WARNING 2,
/// resolved as documented trust; hashing the whole directory is a recorded
/// follow-up, not a change to the SQ-4 scheme). Sibling files are also never
/// treated as candidates: a companion-less DLL is mapped only when its file
/// name AND SHA-256 match <see cref="DefaultTrustAnchor"/>, otherwise it is
/// skipped at debug level without executing or auditing (round 2, WARNING).
/// </para>
/// <para>
/// Refusal-wins (M2T7 reconciliation of the M2T3 semantics): a drop-in
/// candidate refused by any gate returns the plugin_id its manifest claims,
/// and after the scan those ids are unregistered from
/// <see cref="IPluginRegistry"/> — the registry never keeps a same-id entry
/// (DEFAULT or earlier candidate) whose drop-in counterpart was refused in
/// the same cycle. This is what lets the M2 smoke's tampered drop-in claiming
/// <c>imap</c> take the good build-output <c>imap</c> offline until the file
/// is removed and a reload runs (the audit trail says refused; the registry
/// agrees). A sibling-dependency skip is not a refusal and never poisons an
/// id, and DEFAULT-path (<see cref="LoadDefaultsAsync"/>) refusals do not
/// poison either — a valid drop-in must still be able to rescue a broken
/// build-output plugin.
/// </para>
/// </remarks>
public sealed class PluginLoader : IHostedService
{
    /// <summary>Companion-manifest suffix: <c>&lt;assembly-name&gt;.plugin.json</c> (task Risks).</summary>
    internal const string ManifestSuffix = ".plugin.json";

    /// <summary>
    /// Factory client name Program.cs wires for constructor-injected
    /// transport in engine plugins (M3T1): <see cref="CreatePluginInstance"/>
    /// resolves an entry type's <see cref="HttpClient"/> parameter through
    /// <see cref="ActivatorUtilities"/> from that registration.
    /// </summary>
    public const string EngineHttpClientName = "email-agent-plugin-engine";

    private const int UnloadGraceMs = 5000;
    private const int UnloadPollMs = 100;

    private readonly IServiceProvider _services;
    private readonly IOptions<PluginOptions> _options;
    private readonly PluginSignatureVerifier _verifier;
    private readonly PluginManifestValidator _validator;
    private readonly DefaultTrustAnchor _trustAnchor;
    private readonly IPluginRegistry _registry;
    private readonly ILogger<PluginLoader> _logger;

    /// <summary>Serializes whole reload cycles (a monitor lock cannot span the awaits below).</summary>
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    private readonly Lock _sync = new();
    private readonly List<LoadedPlugin> _loaded = new();

    /// <summary>Creates the loader; every dependency is a singleton (registered in Program.cs).</summary>
    public PluginLoader(
        IServiceProvider services,
        IOptions<PluginOptions> options,
        PluginSignatureVerifier verifier,
        PluginManifestValidator validator,
        DefaultTrustAnchor trustAnchor,
        IPluginRegistry registry,
        ILogger<PluginLoader> logger)
    {
        _services = services;
        _options = options;
        _verifier = verifier;
        _validator = validator;
        _trustAnchor = trustAnchor;
        _registry = registry;
        _logger = logger;
    }

    /// <summary>Host startup: one full cycle. Never throws (task step 6).</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReloadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host is shutting down mid-scan — nothing to do.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plugin load cycle failed; continuing without plugins");
        }
    }

    /// <summary>
    /// Graceful drain (task step 6): unregister the current generation, then
    /// unload every collectible context, waiting up to 5 seconds per context
    /// and logging a warning when the runtime has not collected one in time.
    /// Deliberately runs <see cref="SwapAsync"/> with <c>rescan: false</c>: a
    /// shutdown must not re-load plugins or write a second set of
    /// <c>plugin_loaded</c> audit rows for a generation that is being
    /// discarded (root-caused by the M2T3 nested smoke gate: every stop used
    /// to emit one extra <c>plugin_loaded</c> row per DEFAULT plugin).
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var unit in await SwapAsync(rescan: false, CancellationToken.None).ConfigureAwait(false))
        {
            await UnloadAsync(unit, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One swap cycle: the previous generation is returned as unload units
    /// (weak references — strong roots are gone by the time this returns).
    /// <paramref name="rescan"/> is <c>true</c> for startup and SIGHUP
    /// reload (DEFAULT plugins + drop folder repopulate the registry, which
    /// is serialized by <see cref="_reloadGate"/>) and <c>false</c> for the
    /// <see cref="StopAsync"/> drain, which only unregisters and unloads —
    /// re-loading during shutdown would duplicate the audited
    /// <c>plugin_loaded</c> events of the very same generation.
    /// </summary>
    private async Task<List<UnloadUnit>> SwapAsync(bool rescan, CancellationToken cancellationToken)
    {
        List<LoadedPlugin> previous;
        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                previous = _loaded.ToList();
                _loaded.Clear();
            }

            // IPluginRegistry contract: Clear() before a reload; the
            // repopulate below re-registers everything (DEFAULTs first, so a
            // drop-in with the same plugin_id wins — "later candidate wins").
            _registry.Clear();

            if (rescan)
            {
                var options = _options.Value;
                await LoadDefaultsAsync(cancellationToken).ConfigureAwait(false);
                await ScanDropInsAsync(options, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _reloadGate.Release();
        }

        // Convert to weak units while `previous` is still in scope, then let
        // it (and the records it holds) go out of scope before the caller
        // spins the GC — a strong reference here would make every unload time
        // out by construction.
        var units = previous
            .Where(p => p.Context.IsCollectible)
            .Select(p => new UnloadUnit(new WeakReference(p.Context), p.FileName))
            .ToList();
        return units;
    }

    /// <summary>SIGHUP entry point (Program.cs): reload, logging any failure.</summary>
    public async Task Reload()
    {
        try
        {
            await ReloadAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plugin reload after SIGHUP failed");
        }
    }

    /// <summary>Runs one cycle and unloads the previous generation (task step 6).</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        foreach (var unit in await SwapAsync(rescan: true, cancellationToken).ConfigureAwait(false))
        {
            await UnloadAsync(unit, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task LoadDefaultsAsync(CancellationToken cancellationToken)
    {
        foreach (var entry in _trustAnchor.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = entry.AssemblyFileName + ".dll";
            var path = Path.Combine(AppContext.BaseDirectory, fileName);
            if (!File.Exists(path))
            {
                await RejectAsync(
                        targetId: fileName,
                        targetType: "plugin",
                        reason: "load_failed",
                        detail: "missing_default_assembly",
                        message: $"DEFAULT plugin assembly {fileName} is missing from {AppContext.BaseDirectory}",
                        cancellationToken,
                        pluginId: entry.PluginId)
                    .ConfigureAwait(false);
                continue;
            }

            try
            {
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                var sha256 = SHA256.HashData(bytes);

                // Pre-map anchor check: build-output bytes are hashed BEFORE
                // the assembly is mapped, so drifted/tampered bytes are
                // refused without executing anything (module initializers run
                // at load). Only anchored bytes get mapped — and their
                // embedded manifest is inside those bytes.
                if (!entry.Sha256.AsSpan().SequenceEqual(sha256))
                {
                    await RejectAsync(
                            targetId: fileName,
                            targetType: "plugin",
                            reason: PluginSignatureException.Unsigned,
                            detail: "trust_anchor_mismatch",
                            message: $"DEFAULT plugin {entry.PluginId} {entry.Version} SHA-256 does not match "
                                + "DefaultTrustAnchor — the build-output bytes changed; update DefaultTrustAnchor.cs "
                                + "(DefaultTrustAnchorTests fails on the same drift).",
                            cancellationToken,
                            pluginId: entry.PluginId)
                        .ConfigureAwait(false);
                    continue;
                }

                var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                var manifest = ReadEmbeddedManifest(assembly)
                    ?? throw new PluginManifestException(
                        $"DEFAULT plugin {fileName} carries no embedded {entry.AssemblyFileName}{ManifestSuffix} resource");

                if (string.IsNullOrWhiteSpace(manifest.PluginId)
                    || string.IsNullOrWhiteSpace(manifest.Version)
                    || !string.Equals(manifest.AssemblyName, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new PluginManifestException(
                        $"embedded manifest of {fileName} must declare plugin_id/version and assembly == \"{fileName}\"");
                }

                // Defense in depth: the pre-map hash already matched this
                // entry; re-check the manifest identity the anchor claims
                // (the anchor is the ONLY thing that can pre-approve
                // build-output bytes — a rebuilt DLL whose bytes moved was
                // refused above; update DefaultTrustAnchor.cs when
                // DefaultTrustAnchorTests points at the drift).
                var anchored = _trustAnchor.IsDefault(manifest.PluginId, manifest.Version, sha256);
                if (!anchored)
                {
                    await RejectAsync(
                            targetId: manifest.PluginId,
                            targetType: "plugin",
                            reason: PluginSignatureException.Unsigned,
                            detail: "trust_anchor_mismatch",
                            message: $"DEFAULT plugin {manifest.PluginId} {manifest.Version} SHA-256 does not match "
                                + "DefaultTrustAnchor — the build-output bytes changed; update DefaultTrustAnchor.cs "
                                + "(DefaultTrustAnchorTests fails on the same drift).",
                            cancellationToken,
                            pluginId: manifest.PluginId)
                        .ConfigureAwait(false);
                    continue;
                }

                var validation = _validator.Validate(manifest);
                if (!validation.Ok)
                {
                    throw new PluginCapabilityException(
                        validation.Error ?? $"manifest of {fileName} failed validation",
                        manifest.PluginId);
                }

                await RegisterAsync(
                        manifest,
                        assembly,
                        AssemblyLoadContext.GetLoadContext(assembly) ?? AssemblyLoadContext.Default,
                        fileName,
                        bytes,
                        anchored: true,
                        signatureToRecord: Array.Empty<byte>(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            // Typed catches mirror the drop-in path's exception → audit-row
            // mapping so the two sites cannot drift: a DEFAULT candidate that
            // fails the capability or manifest gate is audited under the same
            // manifest_invalid reason/detail taxonomy as any drop-in
            // (nested review round 3, SUGGESTION).
            catch (PluginCapabilityException ex)
            {
                await RejectAsync(
                        targetId: fileName,
                        targetType: "plugin",
                        reason: "manifest_invalid",
                        detail: "capabilities",
                        message: ex.Message,
                        cancellationToken,
                        pluginId: ex.PluginId)
                    .ConfigureAwait(false);
            }
            catch (PluginManifestException ex)
            {
                await RejectAsync(
                        targetId: fileName,
                        targetType: "plugin",
                        reason: "manifest_invalid",
                        detail: "manifest",
                        message: ex.Message,
                        cancellationToken,
                        pluginId: entry.PluginId)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load DEFAULT plugin {File}", fileName);
                await RejectAsync(
                        targetId: fileName,
                        targetType: "plugin",
                        reason: "load_failed",
                        detail: "exception",
                        message: ex.Message,
                        cancellationToken,
                        pluginId: entry.PluginId)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task ScanDropInsAsync(PluginOptions options, CancellationToken cancellationToken)
    {
        var directory = options.PluginsDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            _logger.LogDebug("Plugin directory {Directory} does not exist; no drop-ins loaded", directory);
            return;
        }

        string[] files;
        try
        {
            // Deterministic scan order (M2T3 nested review, INFO): ordinal by
            // file name, and ".dll" matched case-insensitively so a ".DLL"
            // candidate is not silently skipped on Linux. "Later candidate
            // wins" in the registry is only meaningful with a stable order.
            files = Directory
                .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(f => string.Equals(Path.GetExtension(f), ".dll", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate plugin directory {Directory}", directory);
            return;
        }

        var refusedPluginIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var refusedPluginId = await ProcessDropInAsync(directory, file, cancellationToken).ConfigureAwait(false);
            if (refusedPluginId is not null)
            {
                refusedPluginIds.Add(refusedPluginId);
            }
        }

        // Refusal-wins (M2T7, recorded in M2T3/M2T7): a refused drop-in may
        // claim a plugin_id the DEFAULT pass (or an earlier candidate)
        // already registered — the M2 smoke's flow 3 plants exactly that: a
        // tampered EmailAgent.Connectors.Imap.plugin.json claiming
        // plugin_id "imap" beside the good build-output DEFAULT. Unregister
        // AFTER the whole scan so any same-id entry, whenever it registered
        // this cycle, ends the cycle gone — one refusal of an id poisons
        // that id (fail-closed; drop-folder write is operator-only).
        foreach (var pluginId in refusedPluginIds)
        {
            // Presence-checked so the WARNING names a state change that
            // actually happened (review INFO): a refused claim of an id that
            // never registered in this cycle must not announce an
            // "unregistered" that did not occur — it stays at debug level.
            // Both slots are checked and cleaned (M3T1: a refusal poisons the
            // id whether the claimant registered as a source or as an engine).
            var wasRegistered = _registry.GetMessageSource(pluginId) is not null
                || _registry.GetAgentEngine(pluginId) is not null;
            _registry.UnregisterMessageSource(pluginId);
            _registry.UnregisterAgentEngine(pluginId);
            if (wasRegistered)
            {
                _logger.LogWarning(
                    "Plugin {PluginId} unregistered: its drop-in candidate was refused during this reload cycle "
                    + "(refusal-wins — remove the refused file and reload to restore it)",
                    pluginId);
            }
            else
            {
                _logger.LogDebug(
                    "Refused drop-in claimed plugin id {PluginId}, which was not registered at the end of this scan "
                    + "(nothing to unregister)",
                    pluginId);
            }
        }
    }

    /// <summary>
    /// One drop-in candidate through every gate. Returns the plugin_id the
    /// candidate claimed when it was REFUSED (null when accepted, skipped as
    /// a sibling dependency, or the claim was never known) —
    /// <see cref="ScanDropInsAsync"/> unregisters those ids afterwards
    /// (refusal-wins).
    /// </summary>
    private async Task<string?> ProcessDropInAsync(string directory, string file, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(file);
        var manifestFileName = Path.GetFileNameWithoutExtension(fileName) + ManifestSuffix;
        var manifestPath = Path.Combine(directory, manifestFileName);

        // A context created before the gates pass belongs to this method and
        // is dropped again on rejection; on success ownership moves to
        // _loaded (context set to null).
        PluginLoadContext? context = null;

        // Refusal-wins bookkeeping: the plugin_id this candidate claims (set
        // as soon as a manifest states one, so even a later-field validation
        // failure poisons the claimed id) and the id this candidate's refusal
        // must hand back to the scan.
        string? claimedPluginId = null;
        string? refusedPluginId = null;

        try
        {
            var bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            var sha256 = SHA256.HashData(bytes);

            // Gate 1: manifest. The DISK companion is authoritative (gate 2
            // verifies it against these bytes); the embedded copy is only
            // read when no companion exists and is itself untrusted until a
            // later gate approves the candidate.
            PluginManifest manifest;
            string? manifestText = null;
            string? signatureBase64 = null;
            if (File.Exists(manifestPath))
            {
                manifestText = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(manifestText);
                manifest = PluginManifest.FromJsonElement(doc.RootElement);
                if (doc.RootElement.TryGetProperty("signature", out var signatureElement)
                    && signatureElement.ValueKind == JsonValueKind.String)
                {
                    signatureBase64 = signatureElement.GetString();
                }
            }
            else
            {
                // No companion: only a DefaultTrustAnchor-named assembly may
                // be mapped WITHOUT one (its manifest is embedded inside
                // anchored bytes). Every other companion-less DLL in the drop
                // folder is a sibling dependency of some plugin — the
                // documented PluginLoadContext probing workflow — and must be
                // skipped WITHOUT mapping: mapping it here would run its
                // module initializers before any gate and, on every startup
                // and SIGHUP, would emit a plugin_rejected audit row for a
                // refusal that never happened (nested review round 2, WARNING).
                var anchorEntry = _trustAnchor.FindByAssemblyFile(fileName);
                if (anchorEntry is null)
                {
                    _logger.LogDebug(
                        "Skipping {File}: no {Manifest} companion and not a DefaultTrustAnchor assembly "
                        + "(treated as a sibling dependency, not a plugin candidate)",
                        fileName,
                        manifestFileName);
                    return null; // not a candidate, therefore NOT a refusal — never poisons an id
                }

                if (!anchorEntry.Value.Sha256.AsSpan().SequenceEqual(sha256))
                {
                    // Claims a DEFAULT identity by file name but the bytes do
                    // not match the anchor — audited WITHOUT mapping.
                    await RejectAsync(
                            targetId: fileName,
                            targetType: "plugin",
                            reason: PluginSignatureException.Unsigned,
                            detail: "trust_anchor_mismatch",
                            message: $"drop-folder file {fileName} is named like DEFAULT plugin "
                                + $"{anchorEntry.Value.PluginId} {anchorEntry.Value.Version} but its SHA-256 does not "
                                + "match DefaultTrustAnchor — tampered or rebuilt bytes; update DefaultTrustAnchor.cs "
                                + "if this rebuild is intended.",
                            cancellationToken,
                            pluginId: anchorEntry.Value.PluginId)
                        .ConfigureAwait(false);
                    return anchorEntry.Value.PluginId; // refused → poison the DEFAULT id it claims by file name
                }

                context = new PluginLoadContext(directory);
                var assembly = context.LoadFromStream(new MemoryStream(bytes));
                manifest = ReadEmbeddedManifest(assembly)
                    ?? throw new PluginManifestException(
                        $"no {manifestFileName} companion next to {fileName} and the assembly embeds no {ManifestSuffix} resource");
            }

            // Claim the identity as soon as the manifest states one: a
            // refusal must poison this id even when a later validation of
            // the same manifest fails (refusal-wins, M2T7).
            claimedPluginId = string.IsNullOrWhiteSpace(manifest.PluginId) ? null : manifest.PluginId;

            if (string.IsNullOrWhiteSpace(manifest.PluginId) || string.IsNullOrWhiteSpace(manifest.Version))
            {
                throw new PluginManifestException(
                    $"manifest of {fileName} must carry a non-empty plugin_id and version");
            }

            var anchored = _trustAnchor.IsDefault(manifest.PluginId, manifest.Version, sha256);

            // Gate 2: signature over (dll bytes, manifest text). A DISK
            // companion is always checked — never waived by the anchor:
            // DefaultTrustAnchor pre-approves build-output bytes whose
            // manifest is EMBEDDED in those same bytes, while a companion
            // file sits outside them and would otherwise let anyone with
            // drop-folder write pair anchored DLL bytes with an inflated
            // manifest (M2T3 nested review, WARNING 1).
            byte[]? verifiedSignature = null;
            if (manifestText is null)
            {
                if (!anchored)
                {
                    throw new PluginSignatureException(
                        PluginSignatureException.Unsigned,
                        $"no {manifestFileName} companion next to {fileName} and DefaultTrustAnchor does not pre-approve "
                        + $"{manifest.PluginId} {manifest.Version}");
                }
            }
            else
            {
                if (signatureBase64 is null)
                {
                    throw new PluginSignatureException(
                        PluginSignatureException.Unsigned,
                        $"no signature in {manifestFileName} for {fileName}");
                }

                byte[] signature;
                try
                {
                    signature = Convert.FromBase64String(signatureBase64);
                }
                catch (FormatException)
                {
                    throw new PluginSignatureException(
                        PluginSignatureException.SignatureMismatch,
                        $"signature in {manifestFileName} is not valid base64");
                }

                if (!_verifier.Verify(bytes, manifestText, signature))
                {
                    throw new PluginSignatureException(
                        PluginSignatureException.SignatureMismatch,
                        $"HMAC-SHA-256 signature over {fileName} + {manifestFileName} for plugin {manifest.PluginId} "
                        + "did not match — tampered assembly or manifest, or wrong " + PluginOptions.SigningKeyEnvVar);
                }

                verifiedSignature = signature;

                // Gate 3: approval. The anchor still substitutes for it
                // (Q-O5 pre-approval of DEFAULT identities) — but only AFTER
                // the manifest itself authenticated via gate 2.
                if (!anchored
                    && !await IsApprovedAsync(manifest.PluginId, manifest.Version, cancellationToken).ConfigureAwait(false))
                {
                    throw new PluginApprovalException(manifest.PluginId, manifest.Version);
                }
            }

            // Gate 4: capability allow-list (M2 SQ-6 / Q-D6).
            var validation = _validator.Validate(manifest);
            if (!validation.Ok)
            {
                throw new PluginCapabilityException(
                    validation.Error ?? $"manifest of {fileName} failed validation",
                    manifest.PluginId);
            }

            // Gate 5: map the bytes only now that they verified.
            if (context is null)
            {
                context = new PluginLoadContext(directory);
            }

            Assembly loaded;
            try
            {
                loaded = context.LoadFromStream(new MemoryStream(bytes));
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException)
            {
                throw new PluginLoadException($"failed to map {fileName}: {ex.Message}", ex);
            }

            var registered = await RegisterAsync(
                    manifest, loaded, context, fileName, bytes, anchored,
                    verifiedSignature ?? Array.Empty<byte>(), cancellationToken)
                .ConfigureAwait(false);
            if (registered)
            {
                context = null; // owned by _loaded now; swap/stop unload it later
            }
            else
            {
                refusedPluginId = claimedPluginId; // RegisterAsync already audited the refusal
            }
        }
        catch (PluginSignatureException ex)
        {
            refusedPluginId = claimedPluginId;
            await RejectAsync(
                    targetId: fileName,
                    targetType: "plugin",
                    reason: ex.Reason,
                    detail: "signature",
                    message: ex.Message,
                    cancellationToken,
                    pluginId: claimedPluginId)
                .ConfigureAwait(false);
        }
        catch (PluginApprovalException ex)
        {
            refusedPluginId = ex.PluginId;
            await RejectAsync(
                    targetId: fileName,
                    targetType: "plugin",
                    reason: "not_approved",
                    detail: "approval",
                    message: ex.Message,
                    cancellationToken,
                    pluginId: ex.PluginId)
                .ConfigureAwait(false);
        }
        catch (PluginCapabilityException ex)
        {
            refusedPluginId = ex.PluginId;
            await RejectAsync(
                    targetId: fileName,
                    targetType: "plugin",
                    reason: "manifest_invalid",
                    detail: "capabilities",
                    message: ex.Message,
                    cancellationToken,
                    pluginId: ex.PluginId)
                .ConfigureAwait(false);
        }
        catch (PluginManifestException ex)
        {
            refusedPluginId = claimedPluginId;
            await RejectAsync(
                    targetId: fileName,
                    targetType: "plugin",
                    reason: "manifest_invalid",
                    detail: "manifest",
                    message: ex.Message,
                    cancellationToken,
                    pluginId: claimedPluginId)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            refusedPluginId = claimedPluginId;
            await RejectAsync(
                    targetId: fileName,
                    targetType: "plugin",
                    reason: "manifest_invalid",
                    detail: "manifest_malformed",
                    message: ex.Message,
                    cancellationToken,
                    pluginId: claimedPluginId)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            refusedPluginId = claimedPluginId;
            _logger.LogError(ex, "Failed to load plugin candidate {File}", fileName);
            await RejectAsync(
                    targetId: fileName,
                    targetType: "plugin",
                    reason: "load_failed",
                    detail: "exception",
                    message: ex.Message,
                    cancellationToken,
                    pluginId: claimedPluginId)
                .ConfigureAwait(false);
        }
        finally
        {
            if (context is not null && context.IsCollectible)
            {
                context.Unload();
            }
        }

        return refusedPluginId;
    }

    /// <summary>
    /// Gates 6+7: instantiate, register, persist signature + audit. Returns
    /// false when rejected. <paramref name="signatureToRecord"/> is the
    /// signature gate's verified value for a signed drop-in, or an empty
    /// array for an anchored load — anchored DEFAULTs are pre-approved by
    /// <see cref="DefaultTrustAnchor"/>, not by any HMAC, so the SQ-4 record
    /// must not fabricate a "signature" that was never verified (M2T3 nested
    /// review, INFO).
    /// </summary>
    private async Task<bool> RegisterAsync(
        PluginManifest manifest,
        Assembly assembly,
        AssemblyLoadContext context,
        string fileName,
        byte[] bytes,
        bool anchored,
        byte[] signatureToRecord,
        CancellationToken cancellationToken)
    {
        // Everything that can throw while building the audit record happens
        // BEFORE the registry is mutated (nested review round 2, INFO): a
        // metadata-serialization failure must never leave a live registry
        // entry whose AssemblyLoadContext is then unloaded by the caller's
        // finally.
        var sha256 = SHA256.HashData(bytes);
        var pluginLoadedMetadata = new Dictionary<string, object?>
        {
            ["plugin_id"] = manifest.PluginId,
            ["version"] = manifest.Version,
            ["assembly_file"] = fileName,
            ["sha256"] = Convert.ToHexString(sha256).ToLowerInvariant(),
            ["anchored"] = anchored,
            // The GRANTED scope is part of the audited record (M2T3 nested
            // review, WARNING 1 / INFO): what the capability and approval
            // gates evaluated, in the row itself.
            ["capabilities"] = manifest.Capabilities
                .OrderBy(c => c)
                .Select(c => JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(c)) ?? c.ToString())
                .ToArray(),
            ["http_allowlist"] = manifest.HttpAllowlist.ToArray(),
        };

        object instance;
        try
        {
            instance = CreatePluginInstance(assembly, manifest);
        }
        catch (PluginLoadException ex)
        {
            await RejectAsync(
                    targetId: fileName,
                    targetType: "plugin",
                    reason: "load_failed",
                    detail: "no_instantiable_message_source",
                    message: ex.Message,
                    cancellationToken,
                    pluginId: manifest.PluginId)
                .ConfigureAwait(false);
            return false;
        }

        // One slot per plugin — the slot the declared entry type implements.
        // CreatePluginInstance only ever hands back an IAgentEngine (M3T1's
        // openai-compatible DEFAULT) or an IMessageSource (the M2 plugins).
        if (instance is IAgentEngine agentEngine)
        {
            _registry.RegisterAgentEngine(manifest.PluginId, agentEngine);
        }
        else
        {
            _registry.RegisterMessageSource(manifest.PluginId, (IMessageSource)instance);
        }

        lock (_sync)
        {
            _loaded.Add(new LoadedPlugin(manifest, context, fileName));
        }

        // Step 7: persisted signature + audit trail — best-effort so hosts
        // booted without a (migrated) database still serve requests; every
        // failure is logged, never thrown.
        await RecordSignatureBestEffortAsync(manifest, sha256, signatureToRecord, cancellationToken)
            .ConfigureAwait(false);

        await AuditAsync(
                AuditActions.PluginLoaded,
                pluginLoadedMetadata,
                targetId: manifest.PluginId,
                cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Loaded plugin {PluginId} {Version} from {File} (capabilities: {Capabilities})",
            manifest.PluginId,
            manifest.Version,
            fileName,
            string.Join(", ", manifest.Capabilities));

        return true;
    }

    /// <summary>
    /// Gate 6: instantiate the manifest's DECLARED entry type
    /// (<c>plugin.json</c> <c>type</c>) and nothing else. M2T3 nested review
    /// (WARNING 3) removed the silent fallback that registered whatever
    /// instantiable <see cref="IMessageSource"/> happened to be first when
    /// the declared type did not match — a manifest declaring a type the
    /// assembly does not contain is refused, never papered over.
    /// </summary>
    /// <remarks>
    /// M3T1 generalized the gate from <see cref="IMessageSource"/> to the
    /// plugin's slot contract: the declared type must be an instantiable
    /// <see cref="IMessageSource"/> <b>or</b> <see cref="IAgentEngine"/>
    /// implementation (the DEFAULT <c>openai-compatible</c> plugin declares an
    /// engine, which the M2-only gate would have refused). Instantiation tries
    /// the parameterless constructor first — every M2 plugin has one, so their
    /// behaviour is unchanged — and falls back to
    /// <see cref="ActivatorUtilities"/> so an engine can take constructor-injected
    /// services (a plugin-local <see cref="HttpClient"/> from Program.cs's
    /// <c>IHttpClientFactory</c> registration).
    /// </remarks>
    private object CreatePluginInstance(Assembly assembly, PluginManifest manifest)
    {
        List<Type> pluginTypes;
        try
        {
            pluginTypes = assembly.GetTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false })
                .Where(t => typeof(IMessageSource).IsAssignableFrom(t)
                    || typeof(IAgentEngine).IsAssignableFrom(t))
                .ToList();
        }
        catch (ReflectionTypeLoadException ex)
        {
            throw new PluginLoadException(
                $"plugin {manifest.PluginId}: types could not be loaded ({ex.Message})", ex);
        }

        var declared = pluginTypes
            .FirstOrDefault(t => string.Equals(t.FullName, manifest.TypeName, StringComparison.Ordinal));
        if (declared is null)
        {
            throw new PluginLoadException(
                $"plugin {manifest.PluginId}: declared entry type '{manifest.TypeName}' is not an instantiable "
                + "IMessageSource or IAgentEngine implementation in the assembly ("
                + (pluginTypes.Count == 0
                    ? "the assembly declares none"
                    : string.Join(", ", pluginTypes.Select(t => t.FullName)))
                + ")");
        }

        object instance;
        try
        {
            instance = Activator.CreateInstance(declared)!;
        }
        catch (MissingMethodException)
        {
            // No parameterless constructor — resolve the declared one through
            // DI (M3T1: an engine's HttpClient comes from Program.cs).
            try
            {
                instance = ActivatorUtilities.CreateInstance(_services, declared);
            }
            catch (Exception ex)
            {
                throw new PluginLoadException(
                    $"plugin {manifest.PluginId}: declared entry type '{manifest.TypeName}' could not be instantiated "
                    + $"through the service provider: {ex.Message}", ex);
            }
        }
        catch (Exception ex)
        {
            throw new PluginLoadException(
                $"plugin {manifest.PluginId}: declared entry type '{manifest.TypeName}' could not be instantiated", ex);
        }

        if (instance is IMessageSource or IAgentEngine)
        {
            return instance;
        }

        throw new PluginLoadException(
            $"plugin {manifest.PluginId}: declared entry type '{manifest.TypeName}' is not an IMessageSource "
            + "or IAgentEngine");
    }

    /// <summary>Reads the embedded <c>&lt;assembly&gt;.plugin.json</c> resource (DEFAULT plugins ship none on disk).</summary>
    private static PluginManifest? ReadEmbeddedManifest(Assembly assembly)
    {
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(ManifestSuffix, StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
        {
            return null;
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        using var doc = JsonDocument.Parse(reader.ReadToEnd());
        return PluginManifest.FromJsonElement(doc.RootElement);
    }

    /// <summary>Gate 3: a (plugin_id, version) row in <c>plugin_approvals</c>. Fail-closed on any DB error.</summary>
    private async Task<bool> IsApprovedAsync(string pluginId, string version, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();
            return await db.PluginApprovals
                .AnyAsync(a => a.PluginId == pluginId && a.Version == version, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Approval lookup failed for plugin {PluginId} {Version}; rejecting (fail-closed)",
                pluginId,
                version);
            return false;
        }
    }

    /// <summary>
    /// Upserts the <c>plugin_signatures</c> row (SQ-4: verified-at record of
    /// what loaded — the verified HMAC for signed drop-ins, an empty
    /// <c>signature</c> for anchor-pre-approved loads, whose identity is
    /// <c>assembly_sha256</c> against DefaultTrustAnchor). Best-effort:
    /// DB-less or not-yet-migrated hosts log an error and continue.
    /// </summary>
    private async Task RecordSignatureBestEffortAsync(
        PluginManifest manifest,
        byte[] assemblySha256,
        byte[] signature,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<EmailAgentDbContext>();

            var now = DateTimeOffset.UtcNow;
            var updated = await db.PluginSignatures
                .Where(s => s.PluginId == manifest.PluginId && s.Version == manifest.Version)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(s => s.AssemblySha256, assemblySha256)
                    .SetProperty(s => s.Signature, signature)
                    .SetProperty(s => s.LastSeenAt, now), cancellationToken)
                .ConfigureAwait(false);

            if (updated == 0)
            {
                db.PluginSignatures.Add(new PluginSignature
                {
                    Id = Guid.NewGuid(),
                    PluginId = manifest.PluginId,
                    Version = manifest.Version,
                    AssemblySha256 = assemblySha256,
                    Signature = signature,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                });
                try
                {
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (DbUpdateException)
                {
                    // A concurrent host inserted the same (plugin_id, version)
                    // first — the row exists either way; keep the cycle valid.
                    db.ChangeTracker.Clear();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to record signature for plugin {PluginId} {Version}",
                manifest.PluginId,
                manifest.Version);
        }
    }

    private async Task RejectAsync(
        string targetId,
        string targetType,
        string reason,
        string detail,
        string message,
        CancellationToken cancellationToken,
        string? pluginId = null)
    {
        _logger.LogWarning(
            "Rejected plugin candidate {Target}: {Reason} ({Detail}) — {Message}",
            targetId,
            reason,
            detail,
            message);

        var metadata = new Dictionary<string, object?>
        {
            ["assembly_file"] = targetId,
            ["reason"] = reason,
            ["detail"] = detail,
            ["message"] = message,
        };
        if (pluginId is not null)
        {
            metadata["plugin_id"] = pluginId;
        }

        await AuditAsync(AuditActions.PluginRejected, metadata, targetId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task AuditAsync(
        string action,
        Dictionary<string, object?> metadata,
        string targetId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _services.CreateAsyncScope();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
            await audit.LogAsync(
                    new AuditEvent(
                        ActorUserId: null,
                        ActorRole: null,
                        Action: action,
                        TargetType: "plugin",
                        TargetId: targetId,
                        MetadataJson: JsonSerializer.Serialize(metadata),
                        Ip: null),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write audit event {Action} for {Target}", action, targetId);
        }
    }

    private async Task UnloadAsync(UnloadUnit unit, CancellationToken cancellationToken)
    {
        // The strong roots are already gone (registry cleared, _loaded
        // swapped, caller records released) — only the runtime's own
        // bookkeeping keeps the context alive now.
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < UnloadGraceMs && unit.Context.IsAlive)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(UnloadPollMs, cancellationToken).ConfigureAwait(false);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        if (unit.Context.IsAlive)
        {
            _logger.LogWarning(
                "Plugin assembly {File} was not unloaded within {Grace}ms (still referenced)",
                unit.FileName,
                UnloadGraceMs);
        }
    }

    /// <summary>
    /// Test-visible bookkeeping (InternalsVisibleTo: EmailAgent.IntegrationTests):
    /// the current loaded generation — the SIGHUP test takes a
    /// <see cref="WeakReference"/> to a context here BEFORE reloading and must
    /// let the returned record go out of scope before collecting.
    /// </summary>
    internal IReadOnlyList<LoadedPlugin> SnapshotLoaded()
    {
        lock (_sync)
        {
            return _loaded.ToList();
        }
    }

    /// <summary>A collectible context queued for unload (weak on purpose — see <see cref="UnloadAsync"/>).</summary>
    private sealed record UnloadUnit(WeakReference Context, string FileName);

    /// <summary>
    /// Fresh collectible context per drop-in candidate. Managed dependencies
    /// resolve from the default context (returning null from
    /// <see cref="Load(AssemblyName)"/> — shared type identity for
    /// <c>EmailAgent.Contracts</c>); anything else is probed in the plugin
    /// directory via <see cref="AssemblyLoadContext.Resolving"/>, unmanaged
    /// libraries likewise via <see cref="AssemblyLoadContext.LoadUnmanagedDll"/>.
    /// </summary>
    /// <remarks>
    /// Those probed sibling files are loaded WITHOUT a signature check: SQ-4
    /// signs only the plugin's own assembly + companion manifest, so the
    /// directory-level trust assumption is that the drop folder is writable
    /// by operators only (same privilege that places the signed DLL itself).
    /// An attacker with drop-folder write can supply a dependency a signed
    /// plugin references, but cannot alter the signed plugin's own bytes or
    /// manifest fields. Hashing the whole directory per install is recorded
    /// as a follow-up, not part of the M2T3 scheme (nested review, WARNING 2).
    /// </remarks>
    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly string _pluginDirectory;

        public PluginLoadContext(string pluginDirectory)
            : base(name: "plugin:" + Path.GetFileName(pluginDirectory), isCollectible: true)
        {
            _pluginDirectory = pluginDirectory;
            Resolving += ProbePluginDirectory;
        }

        protected override Assembly? Load(AssemblyName assemblyName) => null;

        private Assembly? ProbePluginDirectory(AssemblyLoadContext context, AssemblyName assemblyName)
        {
            if (string.IsNullOrEmpty(assemblyName.Name))
            {
                return null;
            }

            var path = Path.Combine(_pluginDirectory, assemblyName.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = Path.Combine(_pluginDirectory, unmanagedDllName);
            return File.Exists(path) ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }
    }

    /// <summary>
    /// One loaded candidate. Deliberately holds no <see cref="Assembly"/> or
    /// plugin instance: the registry owns the instance, and keeping this
    /// record free of strong assembly roots is what lets a reload actually
    /// collect the old context (the SIGHUP test asserts exactly that).
    /// </summary>
    internal sealed record LoadedPlugin(
        PluginManifest Manifest,
        AssemblyLoadContext Context,
        string FileName);
}
