using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmailAgent.Infrastructure.Audit;

/// <summary>
/// SQ-2's canonical audit HMAC chain (Q-O2 / Q-O10), implemented verbatim so
/// <c>tools/AuditVerify</c> (M1T9) and the test-suite tamper detection agree:
///
/// <list type="bullet">
/// <item><description>genesis = HMAC-SHA256(key, UTF8("email-agent:audit:v1:genesis"))</description></item>
/// <item><description>row.hmac = HMAC-SHA256(key, prevHmac || Serialize(row))</description></item>
/// <item><description>Serialize(row) = System.Text.Json, sorted keys, over id,
/// actor_user_id, actor_role, action, target_type, target_id, metadata_json,
/// ip, created_at</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Serialized values use invariant round-trip formats: Unix seconds for
/// <c>created_at</c>, dotted-quad text for <c>ip</c>, and the raw
/// <c>metadata_json</c> string (already JSON text) for <c>metadata_json</c>.
/// </remarks>
public static class AuditHmacChain
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        // Materialize in declaration order (which the caller supplies sorted
        // in) and keep property names stable across serializer versions.
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // Canonical escaping for the chain: metadata_json values embed raw
        // JSON, so quotes must come out as the conventional \" (the default
        // JavaScriptEncoder escapes them as \u0022). Still JSON-valid — the
        // encoder only relaxes HTML-safety escaping, which is meaningless for
        // HMAC input — and deterministic across .NET versions, which is what
        // tools/AuditVerify needs to reproduce these bytes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly byte[] GenesisLiteral = Encoding.UTF8.GetBytes("email-agent:audit:v1:genesis");

    /// <summary>The 32-byte genesis anchor for an empty chain.</summary>
    public static byte[] ComputeGenesis(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return HMACSHA256.HashData(key, GenesisLiteral);
    }

    /// <summary>The row HMAC for a chain append.</summary>
    public static byte[] ComputeRow(byte[] key, byte[] prevHmac, AuditRowPayload row)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(prevHmac);
        ArgumentNullException.ThrowIfNull(row);

        var serialized = Serialize(row);
        var buffer = new byte[prevHmac.Length + serialized.Length];
        prevHmac.CopyTo(buffer, 0);
        serialized.CopyTo(buffer, prevHmac.Length);

        return HMACSHA256.HashData(key, buffer);
    }

    /// <summary>
    /// Canonical row serialization: a System.Text.Json object with sorted
    /// keys over the nine chained columns.
    /// </summary>
    public static byte[] Serialize(AuditRowPayload row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return JsonSerializer.SerializeToUtf8Bytes(row, SerializerOptions);
    }
}

/// <summary>
/// The nine chained columns of an <c>audit_log</c> row (SQ-2), with the
/// property names exactly as they appear in the serialized form.
/// </summary>
public sealed class AuditRowPayload
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("actor_user_id")]
    public Guid? ActorUserId { get; set; }

    [JsonPropertyName("actor_role")]
    public string? ActorRole { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = null!;

    [JsonPropertyName("target_type")]
    public string? TargetType { get; set; }

    [JsonPropertyName("target_id")]
    public string? TargetId { get; set; }

    /// <summary>Raw <c>metadata_json</c> text (or null).</summary>
    [JsonPropertyName("metadata_json")]
    public string? MetadataJson { get; set; }

    [JsonPropertyName("ip")]
    public string? Ip { get; set; }

    /// <summary>Unix seconds (invariant).</summary>
    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }
}
