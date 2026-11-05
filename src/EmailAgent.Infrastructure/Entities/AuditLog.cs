using System.Net;

namespace EmailAgent.Infrastructure.Entities;

/// <summary>
/// §6 <c>audit_log</c> row: HMAC-chained audit trail (Q-O2). The chain is
/// HMAC-SHA-256 with genesis anchor
/// <c>HMACSHA256(key, "email-agent:audit:v1:genesis")</c> and per-row
/// <c>HMACSHA256(key, prevHmac || Serialize(r))</c> over the sorted-key
/// JSON of <c>id, actor_user_id, actor_role, action, target_type, target_id,
/// metadata_json, ip, created_at</c> (SQ-2 / Q-O10, SQ-8 / Q-O12) — computed
/// by the M1T4 write path, not by this mapping.
/// </summary>
public class AuditLog
{
    /// <summary><c>bigserial</c> PK (M1T2 task step 6).</summary>
    public long Id { get; set; }

    /// <summary>NULL for system events; soft-ish reference kept as Restrict (Q-LP2), dropped in M6aT7.</summary>
    public Guid? ActorUserId { get; set; }

    public string? ActorRole { get; set; }

    public string Action { get; set; } = null!;

    public string? TargetType { get; set; }

    public string? TargetId { get; set; }

    /// <summary>Structured event payload (jsonb); NULL when the event carries none.</summary>
    public string? MetadataJson { get; set; }

    /// <summary><c>inet</c> — client address, NULL for system events.</summary>
    public IPAddress? Ip { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>HMAC-SHA-256, 32 bytes (SQ-8 / Q-O12).</summary>
    public byte[] Hmac { get; set; } = null!;

    public User? Actor { get; set; }
}