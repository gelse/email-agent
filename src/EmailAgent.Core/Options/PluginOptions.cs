using System.Text;

namespace EmailAgent.Core.Options;

/// <summary>
/// M2T3 (§16 / M2 SQ-3 / Q-O5): the <c>Plugin</c> configuration section.
/// <c>SigningKey</c> is the per-install shared plugin-signing key
/// (env <c>PLUGIN__SigningKey</c>; Docker secret in prod, env-var fallback in
/// dev; ≥ 32 UTF-8 bytes, never committed — see <c>.env.example</c>).
/// </summary>
/// <remarks>
/// Lives in <c>EmailAgent.Core.Options</c> — not in Api — for the same reason
/// M1T4 moved <see cref="AuthOptions"/> here: the consumer
/// (<c>EmailAgent.Infrastructure.Plugins.PluginSignatureVerifier</c>) must not
/// need a project reference back into EmailAgent.Api.
/// </remarks>
public sealed class PluginOptions
{
    /// <summary>Configuration section name (env-var form <c>Plugin__*</c> / <c>PLUGIN__*</c>).</summary>
    public const string SectionName = "Plugin";

    /// <summary>Environment variable carrying <see cref="SigningKey"/> (§16 / SQ-3).</summary>
    public const string SigningKeyEnvVar = "PLUGIN__SigningKey";

    /// <summary>Minimum key length in UTF-8 bytes (SQ-3: "≥ 32 bytes").</summary>
    public const int MinSigningKeyBytes = 32;

    /// <summary>Failure message used by both the <c>ValidateOnStart</c> rule and the verifier.</summary>
    /// <remarks>The literal "32" must stay in sync with <see cref="MinSigningKeyBytes"/> (const string interpolation is not a constant expression).</remarks>
    public const string InvalidKeyMessage =
        "Plugin:SigningKey (env PLUGIN__SigningKey) must be present and at least "
        + "32 UTF-8 bytes (§16 / SQ-3 / Q-O5).";

    /// <summary>The plugin drop folder the loader scans (§17). Missing directory = nothing to load.</summary>
    public const string DefaultPluginsDirectory = "/plugins";

    /// <summary>The per-install HMAC-SHA-256 signing key; validated at host start (≥ 32 UTF-8 bytes).</summary>
    public string? SigningKey { get; set; }

    /// <summary>
    /// The directory the loader enumerates for <c>*.dll</c> drop-ins plus their
    /// <c>&lt;assembly&gt;.plugin.json</c> companions (§17). Defaults to
    /// <see cref="DefaultPluginsDirectory"/>; tests point it at a temp directory.
    /// </summary>
    public string PluginsDirectory { get; set; } = DefaultPluginsDirectory;

    /// <summary>Shared length/presence rule for <see cref="SigningKey"/> (≥ 32 UTF-8 bytes).</summary>
    public static bool IsValidSigningKey(string? signingKey) =>
        !string.IsNullOrWhiteSpace(signingKey)
        && Encoding.UTF8.GetBytes(signingKey).Length >= MinSigningKeyBytes;
}
