using System.Net;

namespace EmailAgent.Core.Audit;

/// <summary>
/// One audit event handed to <see cref="IAuditLogger"/> (§15 / Q-O2). The
/// logger persists it as an <c>audit_log</c> row with an HMAC-chain value
/// (SQ-2 canonical chain).
/// </summary>
public sealed record AuditEvent(
    Guid? ActorUserId,
    string? ActorRole,
    string Action,
    string? TargetType,
    string? TargetId,
    string? MetadataJson,
    IPAddress? Ip);
