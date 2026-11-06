using System.Security.Cryptography;
using System.Text;
using EmailAgent.Infrastructure.Audit;
using Npgsql;

// M1T9: HMAC-chain verifier for `audit_log` (SQ-2 canonical definition —
// genesis = HMACSHA256(key, UTF8("email-agent:audit:v1:genesis")), then
// per-row HMACSHA256(key, prevHmac || System.Text.Json sorted-key payload of
// id, actor_user_id, actor_role, action, target_type, target_id,
// metadata_json, ip, created_at)). The serializer is EmailAgent.Infrastructure's
// AuditHmacChain — the same code the write path uses — so the two can never
// disagree on encoding, jsonb normalization, `ip` formatting or timestamp
// precision. This mirrors AuditLogger.VerifyChainAsync against a raw SQL read,
// so it also works when the host's EF layer is unavailable.

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("ERROR: ConnectionStrings__Postgres is not set.");
    return 2;
}

var configuredKey = Environment.GetEnvironmentVariable("AUDIT__HmacKey");
if (string.IsNullOrWhiteSpace(configuredKey))
{
    Console.Error.WriteLine("ERROR: AUDIT__HmacKey is not set.");
    return 2;
}

// Same key derivation as AuditLogger: exactly 32 UTF-8 bytes are used as-is,
// anything else (longer, non-ASCII) is SHA-256'd to a stable 32-byte key.
var raw = Encoding.UTF8.GetBytes(configuredKey);
byte[] key = raw.Length == 32 ? raw : SHA256.HashData(raw);

try
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    await using var command = new NpgsqlCommand(
        """
        SELECT id, actor_user_id, actor_role, action, target_type, target_id,
               metadata_json, host(ip), created_at, hmac
        FROM audit_log
        ORDER BY id ASC
        """,
        connection);

    await using var reader = await command.ExecuteReaderAsync();

    var rows = new List<(AuditRowPayload Row, byte[] Hmac)>();
    while (await reader.ReadAsync())
    {
        var actorUserId = reader.IsDBNull(1) ? default(Guid?) : reader.GetGuid(1);
        var actorRole = reader.IsDBNull(2) ? null : reader.GetString(2);
        var targetType = reader.IsDBNull(4) ? null : reader.GetString(4);
        var targetId = reader.IsDBNull(5) ? null : reader.GetString(5);
        var metadataJson = reader.IsDBNull(6) ? null : reader.GetString(6);
        var ip = reader.IsDBNull(7) ? null : reader.GetString(7);

        var payload = AuditChainVerifier.ToPayload(
            reader.GetInt64(0),
            actorUserId,
            actorRole,
            reader.GetString(3),
            targetType,
            targetId,
            metadataJson,
            ip,
            reader.GetFieldValue<DateTime>(8));

        rows.Add((payload, reader.GetFieldValue<byte[]>(9)));
    }

    var badId = AuditChainVerifier.FindFirstBadRow(rows, key);
    if (badId is not null)
    {
        var bad = rows.First(r => r.Row.Id == badId.Value);
        var expected = AuditHmacChain.ComputeRow(
            key,
            badId == rows[0].Row.Id
                ? AuditHmacChain.ComputeGenesis(key)
                : rows[rows.IndexOf(bad) - 1].Hmac,
            bad.Row);
        Console.WriteLine(
            badId == rows[0].Row.Id && !expected.AsSpan().SequenceEqual(bad.Hmac)
                ? $"FAIL: chain broken at id {badId} (gap or first-row mismatch)"
                : $"FAIL: chain broken at id {badId} "
                + $"(expected HMAC {Convert.ToHexString(expected).ToLowerInvariant()}, "
                + $"got {Convert.ToHexString(bad.Hmac).ToLowerInvariant()})");
        return 1;
    }

    Console.WriteLine($"OK: chain valid, {rows.Count} rows");
    return 0;
}
catch (NpgsqlException ex)
{
    // Exit-code contract: 0 = chain valid, 1 = chain broken, 2 = missing env,
    // 3 = database error — kept distinct from the env guard so "not
    // configured" is never mistaken for "database down".
    Console.Error.WriteLine($"ERROR: database error: {ex.Message}");
    return 3;
}

/// <summary>
/// M1T9: the verification half of the SQ-2 chain, factored out of the top-level
/// statements so the unit suite can exercise the exact walk the CLI runs —
/// genesis seed, per-row <see cref="AuditHmacChain.ComputeRow"/> against the
/// previous stored HMAC, contiguous-id (gap) detection, and first-bad-row
/// reporting — instead of a re-implementation that could drift.
/// </summary>
internal static class AuditChainVerifier
{
    /// <summary>
    /// Walks <paramref name="rows"/> (id order, paired with their stored
    /// HMACs) from genesis. Returns the id of the first bad row — a
    /// non-contiguous id or an HMAC mismatch — or <c>null</c> when intact.
    /// </summary>
    internal static long? FindFirstBadRow(
        IReadOnlyList<(AuditRowPayload Row, byte[] Hmac)> rows,
        byte[] key)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(key);

        var prev = AuditHmacChain.ComputeGenesis(key);
        long lastId = 0;

        for (var i = 0; i < rows.Count; i++)
        {
            var (row, hmac) = rows[i];

            // A gap (deleted rows / non-contiguous ids) means the chain no
            // longer starts from genesis at row 1 — same rule as the writer.
            if (row.Id != lastId + 1)
            {
                return row.Id;
            }

            var expected = AuditHmacChain.ComputeRow(key, prev, row);
            if (!expected.AsSpan().SequenceEqual(hmac))
            {
                return row.Id;
            }

            prev = hmac;
            lastId = row.Id;
        }

        return null;
    }

    /// <summary>
    /// Maps a reader row (the CLI's column order) onto the canonical payload
    /// — including the two conversion rules the chain depends on: Unix
    /// seconds for <c>created_at</c>, and <c>host(ip)</c> already text.
    /// </summary>
    internal static AuditRowPayload ToPayload(
        long id,
        Guid? actorUserId,
        string? actorRole,
        string action,
        string? targetType,
        string? targetId,
        string? metadataJson,
        string? ip,
        DateTime createdAt)
        => new()
        {
            Id = id,
            ActorUserId = actorUserId,
            ActorRole = actorRole,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            MetadataJson = metadataJson,
            Ip = ip,
            CreatedAt = createdAt.Kind == DateTimeKind.Utc
                ? new DateTimeOffset(DateTime.SpecifyKind(createdAt, DateTimeKind.Utc)).ToUnixTimeSeconds()
                : new DateTimeOffset(createdAt.ToUniversalTime()).ToUnixTimeSeconds(),
        };
}
