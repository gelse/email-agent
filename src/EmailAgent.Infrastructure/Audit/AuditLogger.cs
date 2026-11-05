using System.Net;
using System.Text;
using EmailAgent.Core.Audit;
using EmailAgent.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmailAgent.Infrastructure.Audit;

/// <summary>
/// M1T4 audit write path (task step 4): every append serializes chain writers
/// with <c>pg_advisory_xact_lock(hashtext('audit_log_chain'))</c>, reads the
/// current head (genesis when empty), inserts the row (the chain covers the
/// assigned bigserial id, so the HMAC is computed after the insert), and
/// commits — releasing the lock. Joins an ambient transaction when the
/// caller already opened one (RefreshTokenStore rotation reuse path).
/// </summary>
/// <remarks>
/// The optional startup chain walk (task step 4) is surfaced as
/// <see cref="VerifyChainAsync"/>; callers (the host hook) log a structured
/// error instead of crashing the process.
/// </remarks>
public sealed class AuditLogger : IAuditLogger
{
    private readonly EmailAgentDbContext _db;
    private readonly byte[] _key;
    private readonly ILogger<AuditLogger> _logger;

    public AuditLogger(
        EmailAgentDbContext db,
        IOptions<AuditOptions> options,
        ILogger<AuditLogger> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var configured = options?.Value?.HmacKey;
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                "Audit:HmacKey (env AUDIT__HmacKey) must be present (§16 / SQ-2 / Q-O10).");
        }

        // Same length rule as the encryption key: ≥ 32 UTF-8 bytes; longer
        // values are hashed down to a stable 32-byte key (no lossy truncation).
        var raw = Encoding.UTF8.GetBytes(configured);
        _key = raw.Length == 32
            ? raw
            : System.Security.Cryptography.SHA256.HashData(raw);
    }

    public async Task LogAsync(AuditEvent evt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);

        // Join an ambient transaction when the caller already opened one
        // (RefreshTokenStore.RotateAsync revokes the family and writes the
        // reuse-evidence row in the SAME transaction); only open + commit a
        // transaction when this call owns the work.
        var ownsTransaction = _db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await _db.Database.BeginTransactionAsync(ct)
            : null;

        // Serialize chain appends across concurrent LogAsync calls (SQ-2).
        // The xact-level advisory lock rides whichever transaction is active
        // and is released when that transaction commits.
        await _db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtext('audit_log_chain'))",
            ct);

        var prevHmac = await _db.AuditLog
            .OrderByDescending(a => a.Id)
            .Select(a => a.Hmac)
            .FirstOrDefaultAsync(ct)
            ?? AuditHmacChain.ComputeGenesis(_key);

        var row = new AuditLog
        {
            ActorUserId = evt.ActorUserId,
            ActorRole = evt.ActorRole,
            Action = evt.Action,
            TargetType = evt.TargetType,
            TargetId = evt.TargetId,
            MetadataJson = evt.MetadataJson,
            Ip = evt.Ip,
            CreatedAt = DateTimeOffset.UtcNow,
            // Placeholder: the chain covers the assigned bigserial id, which
            // only exists after the insert. Both writes commit together, so
            // no reader ever observes the placeholder (uncommitted rows are
            // invisible under read committed).
            Hmac = new byte[32],
        };

        _db.AuditLog.Add(row);
        await _db.SaveChangesAsync(ct); // assigns row.Id

        // Chain exactly what a verifier will read back: the stored row can
        // differ from the in-memory one (jsonb normalizes metadata_json key
        // order, timestamptz rounds sub-microsecond ticks). HMAC over the
        // DB-sourced payload so write-time bytes == verify-time bytes.
        var stored = await _db.AuditLog
            .AsNoTracking()
            .FirstAsync(a => a.Id == row.Id, ct);

        row.Hmac = AuditHmacChain.ComputeRow(_key, prevHmac, ToPayload(stored));
        _db.AuditLog.Update(row);
        await _db.SaveChangesAsync(ct);

        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        _logger.LogDebug("Audit row {Action} appended as id {AuditId}.", row.Action, row.Id);
    }

    public async Task<long?> VerifyChainAsync(CancellationToken ct)
    {
        var prev = AuditHmacChain.ComputeGenesis(_key);
        var lastId = 0L;

        // Load once and walk in memory: AsAsyncEnumerable needs the
        // Microsoft.EntityFrameworkCore.Core package for the extension and
        // the chain is small (M1 audit volume) — one materialized pass is
        // cheaper than fighting the package graph for a streaming API.
        var rows = await _db.AuditLog
            .AsNoTracking()
            .OrderBy(a => a.Id)
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            if (row.Id != lastId + 1)
            {
                // A gap means rows were deleted or ids were not contiguous —
                // either way the chain no longer starts from genesis at row 1.
                return row.Id;
            }

            var expected = AuditHmacChain.ComputeRow(_key, prev, ToPayload(row));
            if (!expected.AsSpan().SequenceEqual(row.Hmac))
            {
                return row.Id;
            }

            prev = row.Hmac;
            lastId = row.Id;
        }

        return null;
    }

    private static AuditRowPayload ToPayload(AuditLog row) => new()
    {
        Id = row.Id,
        ActorUserId = row.ActorUserId,
        ActorRole = row.ActorRole,
        Action = row.Action,
        TargetType = row.TargetType,
        TargetId = row.TargetId,
        MetadataJson = row.MetadataJson,
        Ip = FormatIp(row.Ip),
        CreatedAt = row.CreatedAt.ToUnixTimeSeconds(),
    };

    private static string? FormatIp(IPAddress? ip) => ip?.ToString();
}