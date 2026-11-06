namespace EmailAgent.Core.Audit;

/// <summary>
/// Failure of the optional startup chain walk: logged as a structured error
/// by the audit writer's host hook (task step 4). It deliberately does not
/// carry row content — operators run <c>tools/AuditVerify</c> (M1T9) for the
/// report.
/// </summary>
public sealed class AuditChainIntegrityException : Exception
{
    public AuditChainIntegrityException(long firstBadId)
        : base($"Audit HMAC chain mismatch at audit_log.id {firstBadId}.")
    {
        FirstBadId = firstBadId;
    }

    /// <summary>The id of the first row whose recomputed HMAC does not match.</summary>
    public long FirstBadId { get; }
}
