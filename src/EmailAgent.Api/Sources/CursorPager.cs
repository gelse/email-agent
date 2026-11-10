using System.Globalization;
using System.Text;
using EmailAgent.Api.Pagination;
using EmailAgent.Infrastructure.Entities;

namespace EmailAgent.Api.Sources;

/// <summary>
/// M2T5 / §13.13: the cursor codec and keyset predicate for the M2–M5 list
/// endpoints (<c>message_sources</c>; extended in M4T3 for <c>action_configs</c>
/// and <c>user_notifications</c>, SQ-M4-6). The payload is base64 of the plain
/// <c>{created_at:O}|{id}</c> pipe form — the v0 window the architecture
/// reserves before M6a's versioned-JSON retrofit (§13.13, SQ-M6a-12); the admin
/// namespace keeps its own base64url-JSON codec in <see cref="Cursor"/>.
/// </summary>
/// <remarks>
/// The cursor is opaque and server-echoed: a strict decode maps any
/// malformed value to <see cref="InvalidCursorException"/> so the endpoint can
/// answer with a §13.12 400 instead of a 500. The wire form is base64url
/// (URL- and query-safe, no padding) for the same reason the admin codec in
/// <see cref="Cursor"/> is: standard base64 emits <c>+</c> and <c>/</c>, and a
/// client that concatenates <c>next_cursor</c> into the query string without
/// percent-encoding would have <c>+</c> decoded as a space — an intermittent
/// 400 on page two. <see cref="Decode"/> still accepts standard base64 so a
/// hand-built cursor round-trips either way. Ordering is
/// <c>(created_at DESC, id DESC)</c> with the strictly-after keyset
/// comparison, so pages are disjoint even when several rows share a
/// <c>created_at</c> (per-row <c>now()</c> ties are broken by <c>id</c>).
/// </remarks>
public static class CursorPager
{
    /// <summary>Encodes the key of the page's last row into the next-page cursor (base64url, unpadded).</summary>
    public static string Encode(DateTimeOffset createdAt, Guid id)
        => ToBase64Url(Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{createdAt.ToString("O", CultureInfo.InvariantCulture)}|{id:D}")));

    /// <summary>
    /// Decodes a cursor produced by <see cref="Encode"/> (base64url) or a
    /// hand-built standard-base64 cursor. Throws
    /// <see cref="InvalidCursorException"/> on any strict-parse failure (bad
    /// base64, missing separator, unparsable timestamp or identifier). A
    /// payload timestamp is normalised to UTC so a non-zero offset can never
    /// reach Npgsql's <c>timestamptz</c> parameter writer (which rejects
    /// <see cref="DateTimeOffset"/> with <c>Offset != 0</c> and would surface
    /// as a 500 instead of the documented 400).
    /// </summary>
    public static (DateTimeOffset CreatedAt, Guid Id) Decode(string cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            throw new InvalidCursorException("The cursor is empty.");
        }

        string payload;
        try
        {
            payload = Encoding.UTF8.GetString(Convert.FromBase64String(FromBase64Url(cursor)));
        }
        catch (FormatException)
        {
            throw new InvalidCursorException("The cursor is not valid base64.");
        }

        var separator = payload.IndexOf('|');
        if (separator < 0 || payload.IndexOf('|', separator + 1) >= 0)
        {
            throw new InvalidCursorException(
                "The cursor payload does not have the expected '<created_at>|<id>' shape.");
        }

        var rawCreatedAt = payload[..separator];
        var rawId = payload[(separator + 1)..];

        if (!DateTimeOffset.TryParseExact(
                rawCreatedAt, "O", CultureInfo.InvariantCulture, DateTimeStyles.None,
                out var createdAt))
        {
            throw new InvalidCursorException("The cursor payload has no valid timestamp.");
        }

        if (!Guid.TryParse(rawId, out var id))
        {
            throw new InvalidCursorException("The cursor payload has no valid identifier.");
        }

        // Npgsql only accepts DateTimeOffset with Offset == 0 for timestamptz;
        // normalising here keeps a legitimate offset usable and stops a crafted
        // cursor from becoming a 500 (it must stay a §13.12 400).
        return (createdAt.ToUniversalTime(), id);
    }

    /// <summary>Standard base64 → base64url: query-safe alphabet, no padding.</summary>
    private static string ToBase64Url(string base64)
        => base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// base64url → standard base64 (padding restored). A cursor already in the
    /// standard alphabet passes through unchanged, so both forms decode.
    /// </summary>
    private static string FromBase64Url(string cursor)
    {
        var standard = cursor.Replace('-', '+').Replace('_', '/');
        return standard.PadRight(standard.Length + ((4 - (standard.Length % 4)) % 4), '=');
    }

    /// <summary>
    /// Applies the §13.13 keyset window to <paramref name="query"/>: the
    /// strictly-after <c>WHERE</c> for a present cursor, the
    /// <c>(created_at DESC, id DESC)</c> ordering, and <c>LIMIT limit + 1</c>
    /// — the extra row is how the caller proves a next page exists without a
    /// second COUNT query. Filters must already be applied by the caller.
    /// </summary>
    public static IQueryable<MessageSource> ApplyCursor(
        IQueryable<MessageSource> query, string? cursor, int limit)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateLimit(limit);

        if (!string.IsNullOrEmpty(cursor))
        {
            var (createdAt, id) = Decode(cursor);
            query = query.Where(
                source => source.CreatedAt < createdAt
                    || (source.CreatedAt == createdAt && source.Id < id));
        }

        return query
            .OrderByDescending(source => source.CreatedAt)
            .ThenByDescending(source => source.Id)
            .Take(limit + 1);
    }

    /// <summary>M4T3 (SQ-M4-6): keyset window for <c>action_configs</c>.</summary>
    public static IQueryable<ActionConfig> ApplyCursor(
        IQueryable<ActionConfig> query, string? cursor, int limit)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateLimit(limit);

        if (!string.IsNullOrEmpty(cursor))
        {
            var (createdAt, id) = Decode(cursor);
            query = query.Where(
                row => row.CreatedAt < createdAt
                    || (row.CreatedAt == createdAt && row.Id < id));
        }

        return query
            .OrderByDescending(row => row.CreatedAt)
            .ThenByDescending(row => row.Id)
            .Take(limit + 1);
    }

    /// <summary>M4T3 (SQ-M4-6): keyset window for <c>user_notifications</c>.</summary>
    public static IQueryable<UserNotification> ApplyCursor(
        IQueryable<UserNotification> query, string? cursor, int limit)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateLimit(limit);

        if (!string.IsNullOrEmpty(cursor))
        {
            var (createdAt, id) = Decode(cursor);
            query = query.Where(
                row => row.CreatedAt < createdAt
                    || (row.CreatedAt == createdAt && row.Id < id));
        }

        return query
            .OrderByDescending(row => row.CreatedAt)
            .ThenByDescending(row => row.Id)
            .Take(limit + 1);
    }

    private static void ValidateLimit(int limit)
    {
        if (limit < PaginationQuery.MinLimit || limit > PaginationQuery.MaxLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                $"The page size must be between {PaginationQuery.MinLimit} and {PaginationQuery.MaxLimit}.");
        }
    }
}
