using System.Text;
using EmailAgent.Infrastructure.Audit;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Audit;

/// <summary>
/// M1T4 unit tests for the canonical audit HMAC chain (SQ-2 / Q-O10):
/// genesis stability, chaining, and tamper detection (task step 10).
/// </summary>
public class AuditHmacChainTests
{
    /// <summary>Ephemeral test key — never a real secret.</summary>
    private const string TestKey = "unit-test-audit-hmac-key-0123456789abcdef";

    private static byte[] Key() => Encoding.UTF8.GetBytes(TestKey);

    [Fact]
    public void Hmac_Of_Genesis_Is_Stable_For_Same_Key()
    {
        var key = Key();

        var first = AuditHmacChain.ComputeGenesis(key);
        var second = AuditHmacChain.ComputeGenesis(key);

        first.Should().HaveCount(32, "HMAC-SHA-256 output is 32 bytes");
        second.Should().Equal(first, "the genesis anchor is deterministic for a given key");

        var otherKey = Encoding.UTF8.GetBytes("another-audit-key-0123456789abcdef");
        AuditHmacChain.ComputeGenesis(otherKey).Should().NotEqual(
            first,
            "a different key must derive a different genesis");
    }

    [Fact]
    public void Chain_Verify_Tampering_Detected()
    {
        var key = Key();
        var (rows, stored) = BuildChain(key, count: 10);

        // Tamper with row 5's metadata_json (1-based id 5 → index 4) AFTER
        // the chain was written — this is the "database row was edited" case.
        rows[4].MetadataJson = "{\"tampered\":true}";

        Verify(rows, stored, key).Should().Be(5, "row 5's HMAC no longer matches its content");
    }

    [Fact]
    public void Chain_Verify_No_Tampering_Returns_Null()
    {
        var key = Key();
        var (rows, stored) = BuildChain(key, count: 10);

        Verify(rows, stored, key).Should().BeNull("every row was chained with its predecessor's stored HMAC");
    }

    [Fact]
    public void Serialize_Uses_Sorted_Keys_And_Stable_Formats()
    {
        var payload = new AuditRowPayload
        {
            Id = 42,
            ActorUserId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            ActorRole = "Admin",
            Action = "login_success",
            TargetType = "user",
            TargetId = "11111111-2222-3333-4444-555555555555",
            MetadataJson = "{\"ok\":true}",
            Ip = "203.0.113.7",
            CreatedAt = 1_700_000_000,
        };

        var json = Encoding.UTF8.GetString(AuditHmacChain.Serialize(payload));

        json.Should().StartWith("{\"id\":", "keys are serialized in declaration (sorted) order");
        json.Should().Contain("\"created_at\":1700000000");
        json.Should().Contain("\"ip\":\"203.0.113.7\"");
        json.Should().Contain("\"metadata_json\":\"{\\\"ok\\\":true}\"");
        json.Should().NotContain("\n", "WriteIndented = false");
    }

    /// <summary>
    /// Writes a clean chain: returns the row contents plus the HMAC each row
    /// was stored with (so a later content edit is detectable).
    /// </summary>
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

    /// <summary>
    /// Walks the chain the way <c>AuditLogger.VerifyChainAsync</c> does and
    /// returns the first id whose recomputed HMAC does not match the stored
    /// one, or <c>null</c> when intact.
    /// </summary>
    private static long? Verify(AuditRowPayload[] rows, byte[][] stored, byte[] key)
    {
        var prev = AuditHmacChain.ComputeGenesis(key);

        for (var i = 0; i < rows.Length; i++)
        {
            var expected = AuditHmacChain.ComputeRow(key, prev, rows[i]);
            if (!expected.AsSpan().SequenceEqual(stored[i]))
            {
                return rows[i].Id;
            }

            prev = stored[i];
        }

        return null;
    }
}
