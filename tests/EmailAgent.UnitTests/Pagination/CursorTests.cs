using EmailAgent.Api.Pagination;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Pagination;

/// <summary>
/// M1T7 unit coverage for the §13.13 pagination helpers: cursor round-trip,
/// strict cursor rejection (→ RFC 7807 400 at the endpoint), and the
/// <c>limit</c> bounds the endpoint maps to a 400 validation failure.
/// </summary>
public class CursorTests
{
    [Fact]
    public void Cursor_EncodeDecode_RoundTrip()
    {
        var createdAt = new DateTimeOffset(2026, 10, 18, 12, 34, 56, TimeSpan.FromHours(2))
            .AddTicks(7_890_000); // sub-second precision must survive
        var id = Guid.NewGuid();

        var encoded = Cursor.Encode(createdAt, id);

        encoded.Should().NotBeNullOrWhiteSpace();
        // base64url alphabet: no '+', '/', or padding.
        encoded.Should().NotContain("+").And.NotContain("/").And.NotContain("=");

        var decoded = Cursor.Decode(encoded);
        decoded.CreatedAt.Should().Be(createdAt);
        decoded.Id.Should().Be(id);
    }

    [Fact]
    public void Cursor_DecodeInvalidString_Throws()
    {
        // Not base64url at all.
        var notBase64 = "%%% definitely not a cursor %%%";
        AssertThrowsCursor(notBase64);

        // Valid base64url that is not JSON.
        var notJson = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("garbage"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        AssertThrowsCursor(notJson);

        // Valid JSON missing the required members.
        var noMembers = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        AssertThrowsCursor(noMembers);

        // Structurally complete but malformed values.
        var badValues = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                "{\"c\":\"not-a-timestamp\",\"i\":\"not-a-guid\"}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        AssertThrowsCursor(badValues);

        // Empty input.
        AssertThrowsCursor(string.Empty);
    }

    [Fact]
    public void Pagination_LimitOverMax_Returns400()
    {
        // Absent / empty parameter → §13.13 default.
        PaginationQuery.TryParseLimit(null, out var limit, out _).Should().BeTrue();
        limit.Should().Be(PaginationQuery.DefaultLimit);
        PaginationQuery.TryParseLimit(string.Empty, out limit, out _).Should().BeTrue();
        limit.Should().Be(PaginationQuery.DefaultLimit);

        // In-range values (bounds inclusive) pass.
        PaginationQuery.TryParseLimit("1", out limit, out _).Should().BeTrue();
        limit.Should().Be(PaginationQuery.MinLimit);
        PaginationQuery.TryParseLimit("100", out limit, out _).Should().BeTrue();
        limit.Should().Be(PaginationQuery.MaxLimit);
        PaginationQuery.TryParseLimit("25", out limit, out _).Should().BeTrue();
        limit.Should().Be(25);

        // Out-of-range / malformed values are rejected with a reason the
        // endpoint surfaces as the RFC 7807 400 `errors` entry for `limit`.
        foreach (var invalid in new[] { "101", "0", "-1", "abc", "10.5" })
        {
            PaginationQuery.TryParseLimit(invalid, out _, out var error)
                .Should().BeFalse($"limit={invalid} is out of range and must be a 400");
            error.Should().NotBeNullOrWhiteSpace();
        }
    }

    private static void AssertThrowsCursor(string value)
    {
        var act = () => Cursor.Decode(value);
        act.Should().Throw<InvalidCursorException>(
            $"'{value}' is not a well-formed cursor and must map to a 400");
    }
}
