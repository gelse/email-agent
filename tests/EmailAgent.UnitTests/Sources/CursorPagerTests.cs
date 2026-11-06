using EmailAgent.Api.Pagination;
using EmailAgent.Api.Sources;
using EmailAgent.Infrastructure.Entities;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Sources;

/// <summary>
/// M2T5 unit coverage for the §13.13 cursor codec and keyset predicate of the
/// <c>/v1/user/sources</c> list: the plain <c>{created_at:O}|{id}</c> pipe
/// payload the task pins (distinct from the admin namespace's base64url-JSON
/// codec), strict rejection of anything malformed (→ 400, never a 500), and
/// the <c>(created_at DESC, id DESC)</c> window with its <c>limit + 1</c>
/// over-fetch.
/// </summary>
public class CursorPagerTests
{
    [Fact]
    public void CursorPager_Encode_WritesBase64OfPipePayload()
    {
        var createdAt = new DateTimeOffset(
            2026, 11, 4, 8, 30, 15, TimeSpan.Zero).AddTicks(1_234_500);
        var id = Guid.NewGuid();

        var encoded = CursorPager.Encode(createdAt, id);

        // Wire form is base64url (query-safe alphabet, no padding): a client
        // may concatenate next_cursor into the query string verbatim.
        encoded.Should().MatchRegex("^[A-Za-z0-9_-]+$",
            "the cursor must be pure base64url so it round-trips unencoded in a query string");

        var standard = encoded.Replace('-', '+').Replace('_', '/')
            .PadRight(encoded.Length + ((4 - (encoded.Length % 4)) % 4), '=');
        var payload = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(standard));
        payload.Should().Be(
            $"{createdAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)}|{id:D}",
            "the task file pins base64({created_at:O}|{id}) as the wire cursor format");
    }

    [Fact]
    public void CursorPager_Decode_AcceptsStandardBase64_AsWellAsBase64Url()
    {
        var createdAt = new DateTimeOffset(2026, 11, 4, 8, 30, 15, TimeSpan.Zero);
        var id = Guid.NewGuid();
        var payload = $"{createdAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)}|{id:D}";
        var standardBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload));

        var decoded = CursorPager.Decode(standardBase64);

        decoded.CreatedAt.Should().Be(createdAt);
        decoded.Id.Should().Be(id);
    }

    [Fact]
    public void CursorPager_Decode_NonUtcOffset_IsNormalisedToUtc()
    {
        // Npgsql rejects DateTimeOffset with Offset != 0 for timestamptz; a
        // crafted cursor must degrade to an equivalent UTC instant, never a 500.
        var offset = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.FromHours(2));
        var id = Guid.NewGuid();
        var payload = $"{offset.ToString("O", System.Globalization.CultureInfo.InvariantCulture)}|{id:D}";
        var cursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload));

        var decoded = CursorPager.Decode(cursor);

        decoded.CreatedAt.Offset.Should().Be(TimeSpan.Zero,
            "the decoded key must be UTC before it reaches a timestamptz parameter");
        decoded.CreatedAt.Should().Be(offset.ToUniversalTime());
        decoded.Id.Should().Be(id);
    }

    [Fact]
    public void CursorPager_EncodeDecode_RoundTrips()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();

        var decoded = CursorPager.Decode(CursorPager.Encode(createdAt, id));

        decoded.CreatedAt.Should().Be(createdAt);
        decoded.Id.Should().Be(id);
    }

    [Fact]
    public void CursorPager_Decode_MalformedValues_ThrowInvalidCursor()
    {
        // Not base64 at all.
        AssertThrowsCursor("%%% not a cursor %%%");

        // Base64 without the '<created_at>|<id>' shape.
        AssertThrowsCursor(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("garbage")));

        // Two separators are ambiguous — rejected.
        AssertThrowsCursor(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            $"{DateTimeOffset.UtcNow:O}|{Guid.NewGuid():D}|extra")));

        // Well-shaped but unparsable timestamp.
        AssertThrowsCursor(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            $"yesterday|{Guid.NewGuid():D}")));

        // Well-shaped but unparsable identifier.
        AssertThrowsCursor(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            $"{DateTimeOffset.UtcNow:O}|not-a-guid")));

        // Empty input.
        AssertThrowsCursor(string.Empty);
    }

    [Fact]
    public void CursorPager_ApplyCursor_OrdersDescending_AndTakesLimitPlusOne()
    {
        var rows = BuildRows(count: 5);
        var query = rows.AsQueryable();

        // limit + 1 is the has-more probe the service counts before trimming.
        var page = CursorPager.ApplyCursor(query, cursor: null, limit: 2).ToList();

        page.Should().HaveCount(3, "the extra row proves a next page exists");
        page.Select(r => r.CreatedAt)
            .Should().BeInDescendingOrder("the keyset order is created_at DESC, id DESC");
        page.Select(r => r.Id).Should().Equal(
            rows.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
                .Take(3).Select(r => r.Id));
    }

    [Fact]
    public void CursorPager_ApplyCursor_ContinuesStrictlyAfter_TheKey()
    {
        var rows = BuildRows(count: 5);
        var query = rows.AsQueryable().OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id).AsQueryable();
        var ordered = query.ToList();

        var firstPage = ordered.Take(2).ToList();
        var cursor = CursorPager.Encode(firstPage[^1].CreatedAt, firstPage[^1].Id);

        var secondPage = CursorPager.ApplyCursor(query, cursor, limit: 2).ToList();

        secondPage.Select(r => r.Id).Should().NotIntersectWith(
            firstPage.Select(r => r.Id), "pages must be disjoint");
        secondPage.Select(r => r.Id).Should().Equal(
            ordered.Skip(2).Take(3).Select(r => r.Id),
            "the window continues exactly where the previous page stopped");
    }

    [Fact]
    public void CursorPager_ApplyCursor_TiedCreatedAts_AreBrokenById()
    {
        // Per-row now() ties are possible: id DESC must keep pages disjoint.
        var tied = new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);
        var rows = Enumerable.Range(0, 4)
            .Select(_ => new MessageSource { Id = Guid.NewGuid(), CreatedAt = tied })
            .ToList();
        var query = rows.AsQueryable();

        // ApplyCursor over-fetches limit + 1 rows (the has-more probe); the
        // service trims the probe before serving, and the probe row re-enters
        // the next window — so the served page here is the first 2 ids.
        var first = CursorPager.ApplyCursor(query, cursor: null, limit: 2)
            .Select(r => r.Id).Take(2).ToList();
        var cursor = CursorPager.Encode(tied, first[1]);
        var second = CursorPager.ApplyCursor(query, cursor, limit: 2)
            .Select(r => r.Id).ToList();

        first.Should().BeInDescendingOrder();
        second.Should().NotIntersectWith(first);
        first.Concat(second).Should().BeEquivalentTo(rows.Select(r => r.Id),
            "every tied row is reachable exactly once across the pages");
    }

    [Fact]
    public void CursorPager_ApplyCursor_PageSizeOutOfRange_Throws()
    {
        var query = Array.Empty<MessageSource>().AsQueryable();

        var belowMin = () => CursorPager.ApplyCursor(query, null, limit: 0);
        var aboveMax = () => CursorPager.ApplyCursor(query, null, limit: PaginationQuery.MaxLimit + 1);

        belowMin.Should().Throw<ArgumentOutOfRangeException>();
        aboveMax.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static void AssertThrowsCursor(string cursor)
    {
        var decode = () => CursorPager.Decode(cursor);
        decode.Should().Throw<InvalidCursorException>(
            "a malformed cursor must surface as a 400 validation failure, never a 500");
    }

    /// <summary>Rows with strictly decreasing created_at (fresh newest-first order).</summary>
    private static List<MessageSource> BuildRows(int count)
    {
        var baseTime = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, count)
            .Select(i => new MessageSource
            {
                Id = Guid.NewGuid(),
                CreatedAt = baseTime.AddSeconds(-i),
            })
            .ToList();
    }
}
