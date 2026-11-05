using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmailAgent.Contracts.Plugins;

/// <summary>
/// The permission grants a plugin may request in its `plugin.json` manifest
/// (§9 "Plugin discovery and isolation"; M2 SQ-1 / Q-C6). Each bit maps to a
/// permission the loader checks at load time; <c>HttpEgress</c> is the grant of
/// the right to make outbound HTTP while the manifest's <c>http_allowlist</c>
/// is the scope. Backed by <see langword="long"/> deliberately rather than
/// <see langword="int"/> — the capability set may exceed 32 bits in later
/// milestones (M2T1 Risks).
/// </summary>
[Flags]
[JsonConverter(typeof(CapabilityJsonConverter))]
public enum Capability : long
{
    /// <summary>No grants.</summary>
    None = 0,

    /// <summary>Read blob objects by id.</summary>
    BlobReadById = 1,

    /// <summary>Write blob objects the plugin owns.</summary>
    BlobWriteByOwner = 2,

    /// <summary>Write rows into <c>processed_messages</c> (§6).</summary>
    ProcessedMessagesWrite = 4,

    /// <summary>Make outbound HTTP calls, scoped by the manifest's <c>http_allowlist</c>.</summary>
    HttpEgress = 8,

    /// <summary>Use the plugin's scratch directory (<c>/tmp/&lt;plugin-id&gt;</c>).</summary>
    ScratchDir = 16,

    /// <summary>Encrypt user data with the user's DEK (M1T3).</summary>
    EncryptWithUserDek = 32,

    /// <summary>Encrypt with the system key.</summary>
    EncryptWithSystemKey = 64,
}

/// <summary>
/// JSON contract for <see cref="Capability"/>: snake_case strings matching the
/// `plugin.json` example (`"processed_messages_write"`, `"http_egress"` —
/// M2 SQ-1), with integer values tolerated for forward compatibility.
/// </summary>
/// <remarks>
/// .NET 8 has no <c>JsonStringEnumConverterAttribute</c>, so the converter is
/// attached with <c>[JsonConverter(typeof(...))]</c> on the enum itself and
/// implemented as a named subclass of <c>JsonStringEnumConverter&lt;Capability&gt;</c>
/// that supplies the snake_case naming policy.
/// </remarks>
public sealed class CapabilityJsonConverter : JsonStringEnumConverter<Capability>
{
    /// <summary>Creates the converter with <c>snake_case</c> naming and integer values allowed.</summary>
    public CapabilityJsonConverter()
        : base(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: true)
    {
    }
}

/// <summary>
/// Helpers for composing and inspecting <see cref="Capability"/> flag sets
/// (M2T1 step 4; consumed by M2T3's manifest validator).
/// </summary>
public static class CapabilityExtensions
{
    /// <summary>
    /// Union of all declared capability bits. Parsing code rejects values
    /// outside this mask so unknown bits can never pass silently
    /// (<see cref="Decompose"/> alone would drop them). Kept in sync with the
    /// enum by <c>Capability_DeclaredMask_Covers_All_Declared_Members</c>.
    /// </summary>
    public const Capability DeclaredMask =
        Capability.BlobReadById | Capability.BlobWriteByOwner |
        Capability.ProcessedMessagesWrite | Capability.HttpEgress |
        Capability.ScratchDir | Capability.EncryptWithUserDek |
        Capability.EncryptWithSystemKey;

    /// <summary>
    /// Returns the individual set bits of <paramref name="value"/> in
    /// declaration order; <see cref="Capability.None"/> yields an empty
    /// sequence. Bits set in <paramref name="value"/> that do not correspond to
    /// a declared capability are not returned (the M2T3 validator rejects
    /// unknown bits separately).
    /// </summary>
    public static IEnumerable<Capability> Decompose(this Capability value) =>
        Enum.GetValues<Capability>().Where(bit => bit != Capability.None && value.HasFlag(bit));
}
