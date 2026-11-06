using System.Security.Cryptography;
using System.Text;
using EmailAgent.Infrastructure.Audit;
using FluentAssertions;
using Xunit;
// The CLI's verification walk lives in the AuditVerify assembly's global
// namespace (top-level Program.cs + internal static AuditChainVerifier).

namespace EmailAgent.UnitTests.Audit;

/// <summary>
/// M1T9 unit coverage for <c>tools/AuditVerify</c>'s verification loop. The
/// tests target <c>AuditChainVerifier</c> — the exact walk the CLI binary
/// runs (internal, visible via InternalsVisibleTo) — so a drift in the
/// SQL-to-payload mapping or the gap/HMAC rules is caught here rather than
/// only by the live smoke. The binary's env binding and Npgsql reader stay
/// out of unit scope; the smoke exercises them against Postgres.
/// </summary>
public class AuditVerifyToolTests
{
    /// <summary>Ephemeral test key — never a real secret.</summary>
    private const string TestKey = "unit-test-audit-verify-key-0123456789abcdef";

    private static byte[] Key() => Encoding.UTF8.GetBytes(TestKey);

    [Fact]
    public void Verify_Genesis_Matches_SQ2_Literal()
    {
        var key = Key();
        var genesis = AuditHmacChain.ComputeGenesis(key);
        var expected = HMACSHA256.HashData(
            key, Encoding.UTF8.GetBytes("email-agent:audit:v1:genesis"));

        genesis.Should().Equal(expected, "the SQ-2 genesis anchor is fixed");
    }

    [Fact]
    public void Verify_CleanChain_ReturnsNull_And_ZeroBadRows()
    {
        var key = Key();
        var (rows, stored) = BuildChain(key, count: 5);

        VerifyChain(rows, stored, key).Should().BeNull("no row was tampered with");
    }

    [Fact]
    public void Verify_TamperedRow_ReportsFirstBadId()
    {
        var key = Key();
        var (rows, stored) = BuildChain(key, count: 5);

        // Row 3 (1-based id 3) was edited after the chain was written.
        rows[2].MetadataJson = "{\"tampered\":true}";

        VerifyChain(rows, stored, key).Should().Be(
            3, "the walk stops at the first row whose recomputed HMAC differs");
    }

    [Fact]
    public void Verify_GapInIds_ReportsTheGapRow()
    {
        var key = Key();
        var (rows, stored) = BuildChain(key, count: 4);

        // Simulate a deleted row: ids 1, 2, 4, 5 — row 4 breaks contiguity
        // after 2.
        rows[2].Id = 4;
        rows[3].Id = 5;

        VerifyChain(rows, stored, key).Should().Be(
            4, "a non-contiguous id is a chain break (rows were removed)");
    }

    [Fact]
    public void ToPayload_MapsReaderColumns_AndUnixSeconds()
    {
        var createdAt = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        var payload = AuditChainVerifier.ToPayload(
            id: 7,
            actorUserId: Guid.Parse("11111111-2222-3333-4444-555555555555"),
            actorRole: "Admin",
            action: "login_success",
            targetType: "user",
            targetId: "11111111-2222-3333-4444-555555555555",
            metadataJson: "{\"email\":\"a@b.c\"}",
            ip: "203.0.113.7",
            createdAt: createdAt);

        payload.Id.Should().Be(7);
        payload.CreatedAt.Should().Be(
            new DateTimeOffset(createdAt).ToUnixTimeSeconds(),
            "created_at is chained as Unix seconds (AuditHmacChain's contract)");
        payload.Ip.Should().Be("203.0.113.7", "host(ip) text passes through");
        payload.MetadataJson.Should().Be("{\"email\":\"a@b.c\"}");

        // Npgsql's timestamptz reader hands back a Local-kind DateTime holding
        // the same instant — pin that branch too so a kind/TZ regression
        // cannot slip through the reader path the CLI actually takes.
        // (Same instant via ToLocalTime keeps the assertion TZ-independent.)
        var localCreatedAt = createdAt.ToLocalTime();
        localCreatedAt.Kind.Should().Be(DateTimeKind.Local,
            "the reader branch under test must be Local-kind");
        var localPayload = AuditChainVerifier.ToPayload(
            id: 7,
            actorUserId: Guid.Parse("11111111-2222-3333-4444-555555555555"),
            actorRole: "Admin",
            action: "login_success",
            targetType: "user",
            targetId: "11111111-2222-3333-4444-555555555555",
            metadataJson: "{\"email\":\"a@b.c\"}",
            ip: "203.0.113.7",
            createdAt: localCreatedAt);
        localPayload.CreatedAt.Should().Be(
            payload.CreatedAt,
            "the Local-kind reader branch normalizes to the same Unix seconds");
    }

    /// <summary>
    /// Walks the chain exactly as <c>AuditChainVerifier.FindFirstBadRow</c>
    /// (and hence the CLI) does: genesis seed, per-row ComputeRow against the
    /// previous stored HMAC, contiguous-id check.
    /// </summary>
    private static long? VerifyChain(AuditRowPayload[] rows, byte[][] stored, byte[] key)
    {
        var paired = rows.Zip(stored, (row, hmac) => (row, hmac)).ToList();
        return AuditChainVerifier.FindFirstBadRow(paired, key);
    }

    private static (AuditRowPayload[] Rows, byte[][] Stored) BuildChain(byte[] key, int count)
    {
        var rows = new AuditRowPayload[count];
        var stored = new byte[count][];
        var prev = AuditHmacChain.ComputeGenesis(key);

        for (var i = 0; i < count; i++)
        {
            var row = new AuditRowPayload
            {
                Id = i + 1,
                ActorUserId = Guid.NewGuid(),
                ActorRole = i % 2 == 0 ? "Admin" : "User",
                Action = "login_success",
                TargetType = "user",
                TargetId = Guid.NewGuid().ToString(),
                MetadataJson = $"{{\"n\":{i}}}",
                Ip = $"203.0.113.{i + 1}",
                CreatedAt = 1_700_000_000 + i,
            };

            var hmac = AuditHmacChain.ComputeRow(key, prev, row);
            rows[i] = row;
            stored[i] = hmac;
            prev = hmac;
        }

        return (rows, stored);
    }
}
