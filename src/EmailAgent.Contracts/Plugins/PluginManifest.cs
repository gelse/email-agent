using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmailAgent.Contracts.Plugins;

/// <summary>
/// The parsed `plugin.json` manifest every connector plugin ships
/// (§9 "Plugin discovery and isolation"; M2 SQ-1 / Q-C6). Canonical JSON form:
/// <c>{"plugin_id","version","assembly","type","capabilities":[snake_case
/// names],"http_allowlist":[fqdn...],"notes"}</c>. Loading from disk is
/// M2T3's job; this record only defines the shape plus the element/file
/// parsers M2T1 is scoped to (M2T1 step 13).
/// </summary>
/// <remarks>
/// <c>capabilities</c> is typed <see cref="IReadOnlySet{T}"/> per SQ-1;
/// deserializing that interface is not supported by System.Text.Json on
/// net10.0, so <see cref="FromJsonElement(JsonElement)"/> parses manually —
/// always use these parsers instead of <c>JsonSerializer.Deserialize&lt;PluginManifest&gt;</c>.
/// </remarks>
public record PluginManifest(
    [property: JsonPropertyName("plugin_id")] string PluginId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("assembly")] string AssemblyName,
    [property: JsonPropertyName("type")] string TypeName,
    [property: JsonPropertyName("capabilities")] IReadOnlySet<Capability> Capabilities,
    [property: JsonPropertyName("http_allowlist")] IReadOnlyList<string> HttpAllowlist,
    [property: JsonPropertyName("notes")] string? Notes = null)
{
    /// <summary>
    /// Reads <c>plugin.json</c> from <paramref name="path"/> and parses it.
    /// </summary>
    /// <exception cref="JsonException">The file is not valid JSON or not a valid manifest.</exception>
    /// <exception cref="FileNotFoundException">The manifest file does not exist.</exception>
    /// <exception cref="DirectoryNotFoundException">The manifest directory does not exist.</exception>
    /// <exception cref="IOException">The manifest file could not be read (including <see cref="UnauthorizedAccessException"/>).</exception>
    /// <remarks>
    /// Only malformed manifests throw <see cref="JsonException"/> — IO failures
    /// propagate as their own exception family so the M2T3 loader can choose
    /// its skip/abort policy deliberately.
    /// </remarks>
    public static PluginManifest FromJsonFile(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return FromJsonElement(doc.RootElement);
    }

    /// <summary>
    /// Parses a manifest from a JSON element. <c>plugin_id</c>, <c>version</c>,
    /// <c>assembly</c> and <c>type</c> are required non-empty-kinded strings;
    /// <c>capabilities</c> and <c>http_allowlist</c> default to empty when
    /// absent (an absent capability set grants nothing — the loader validates
    /// grants in M2T3); <c>notes</c> is optional. Any present-but-mistyped
    /// member throws <see cref="JsonException"/>, as do the legacy pre-SQ-1
    /// field names <c>id</c> and <c>required_capabilities</c> (the error names
    /// their replacement: <c>plugin_id</c> / <c>capabilities</c>)
    /// (decision Q-C9, 2026-11-07).
    /// </summary>
    public static PluginManifest FromJsonElement(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw new JsonException($"plugin manifest must be a JSON object, was {el.ValueKind}");

        // Pre-SQ-1 legacy names are otherwise read as absent members: a
        // manifest authored with 'required_capabilities' silently yields an
        // empty capability set (zero-grant), and a legacy 'id' reports a
        // generic missing-'plugin_id' error that never names the stale field
        // (plans/implementation/M2T1-connector-framework-contracts.md, deviation
        // 6). Reject them loudly, naming the replacement — human decision 11.
        if (el.TryGetProperty("id", out _))
            throw new JsonException(
                "plugin manifest uses legacy field 'id'; use 'plugin_id' per the M2 SQ-1 manifest schema");
        if (el.TryGetProperty("required_capabilities", out _))
            throw new JsonException(
                "plugin manifest uses legacy field 'required_capabilities'; use 'capabilities' per the M2 SQ-1 manifest schema");

        string RequiredString(string name)
        {
            if (!el.TryGetProperty(name, out var value))
                throw new JsonException($"plugin manifest is missing required field '{name}'");
            if (value.ValueKind != JsonValueKind.String)
                throw new JsonException($"plugin manifest field '{name}' must be a string");
            return value.GetString()!;
        }

        IReadOnlySet<Capability> capabilities = ImmutableHashSet<Capability>.Empty;
        if (el.TryGetProperty("capabilities", out var capabilitiesEl))
        {
            if (capabilitiesEl.ValueKind != JsonValueKind.Array)
                throw new JsonException("plugin manifest field 'capabilities' must be an array");

            var builder = ImmutableHashSet.CreateBuilder<Capability>();
            foreach (var entry in capabilitiesEl.EnumerateArray())
            {
                // The converter accepts combined comma-separated values, so
                // decompose into individual bits: a set holding a composite
                // would break Contains/SetEquals-based grant checks (M2T1
                // review fix). Unknown bits are rejected, never dropped.
                var parsed = JsonSerializer.Deserialize<Capability>(entry.GetRawText());
                var unknown = parsed & ~CapabilityExtensions.DeclaredMask;
                if (unknown != Capability.None)
                    throw new JsonException(
                        $"plugin manifest field 'capabilities' contains unknown bits: 0x{(long)unknown:X}");
                foreach (var bit in parsed.Decompose())
                    builder.Add(bit);
            }
            capabilities = builder.ToImmutable();
        }

        IReadOnlyList<string> httpAllowlist = Array.Empty<string>();
        if (el.TryGetProperty("http_allowlist", out var allowlistEl))
        {
            if (allowlistEl.ValueKind != JsonValueKind.Array)
                throw new JsonException("plugin manifest field 'http_allowlist' must be an array");

            var entries = new List<string>();
            foreach (var entry in allowlistEl.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String)
                    throw new JsonException("plugin manifest field 'http_allowlist' must contain only strings");
                entries.Add(entry.GetString()!);
            }
            httpAllowlist = entries;
        }

        string? notes = null;
        if (el.TryGetProperty("notes", out var notesEl) && notesEl.ValueKind != JsonValueKind.Null)
        {
            if (notesEl.ValueKind != JsonValueKind.String)
                throw new JsonException("plugin manifest field 'notes' must be a string or null");
            notes = notesEl.GetString();
        }

        return new PluginManifest(
            RequiredString("plugin_id"),
            RequiredString("version"),
            RequiredString("assembly"),
            RequiredString("type"),
            capabilities,
            httpAllowlist,
            notes);
    }
}
