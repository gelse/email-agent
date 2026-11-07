using System.Text;
using EmailAgent.Api.Models;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Models;

/// <summary>
/// M3T2 — <see cref="Utf8Excerpt.Truncate"/> never splits a multi-byte
/// sequence at the 256-byte probe budget (SQ-M3-3; task-named unit test).
/// </summary>
[Trait("Category", "M3T2")]
public class Utf8ExcerptTests
{
    [Fact]
    public void Utf8Excerpt_Truncate_Utf8SafeBoundary()
    {
        // Shorter-than-budget values pass through unchanged.
        Utf8Excerpt.Truncate("ping", 256).Should().Be("ping");
        Utf8Excerpt.Truncate(string.Empty, 256).Should().BeEmpty();

        // 2-byte characters: an odd budget lands mid-sequence, so the cut must
        // walk back to the last complete character (15 → 14 bytes = 7 chars).
        var twoByte = new string('é', 10); // 20 UTF-8 bytes
        var truncatedTwoByte = Utf8Excerpt.Truncate(twoByte, 15);
        truncatedTwoByte.Should().Be(
            new string('é', 7), "the cut backs off the continuation byte instead of splitting é");
        Encoding.UTF8.GetByteCount(truncatedTwoByte).Should().Be(14);
        truncatedTwoByte.Should().NotContain("�", "a split sequence must never yield a replacement character");

        // 3-byte characters: budget 4 lands inside the second 好 (bytes 4-6),
        // so the cut walks back to the first character (3 bytes).
        var threeByte = new string('好', 3); // 9 UTF-8 bytes
        var truncatedThreeByte = Utf8Excerpt.Truncate(threeByte, 4);
        truncatedThreeByte.Should().Be("好");
        truncatedThreeByte.Should().NotContain("�");

        // A budget exactly at a boundary is honored as-is.
        Utf8Excerpt.Truncate(threeByte, 6).Should().Be("好好");

        // Degenerate budgets stay safe (including negatives — the documented
        // "values <= 0 yield an empty string" contract, not an exception).
        Utf8Excerpt.Truncate(threeByte, 0).Should().BeEmpty();
        Utf8Excerpt.Truncate(threeByte, -1).Should().BeEmpty();
        Utf8Excerpt.Truncate(string.Empty, -1).Should().BeEmpty();
    }
}
