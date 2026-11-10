using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmailAgent.Api.Pagination;

/// <summary>
/// M1T7 / §13.13 (Q-API7): the opaque cursor for the shared
/// <c>(created_at DESC, id DESC)</c> ordering. The payload is base64url of
/// the JSON object <c>{"c":"&lt;timestamptz&gt;","i":"&lt;uuid&gt;"}</c> —
/// server-echoed, never offset-based, and strict on decode: anything that is
/// not a well-formed cursor is a 400 validation failure per §13.12 (the
/// endpoint maps <see cref="InvalidCursorException"/>).
/// </summary>
public static class Cursor
{
    /// <summary>
    /// Encodes the key of the last returned row into the next-page cursor.
    /// The DateTimeOffset round-trips through ISO 8601 with offset (ticks
    /// preserved) so the decoded key compares equal to the stored column.
    /// </summary>
    public static string Encode(DateTimeOffset createdAt, Guid id)
    {
        var json = JsonSerializer.Serialize(new Payload
        {
            CreatedAt = createdAt.ToString("O"),
            Id = id.ToString(),
        });
        return ToUrlSafeBase64(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// Decodes a cursor produced by <see cref="Encode"/>. Throws
    /// <see cref="InvalidCursorException"/> on any strict-parse failure
    /// (bad base64url, bad JSON, missing/malformed members).
    /// </summary>
    public static (DateTimeOffset CreatedAt, Guid Id) Decode(string cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            throw new InvalidCursorException("The cursor is empty.");
        }

        byte[] json;
        try
        {
            json = FromUrlSafeBase64(cursor);
        }
        catch (FormatException)
        {
            throw new InvalidCursorException("The cursor is not valid base64url.");
        }

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(json);
        }
        catch (JsonException)
        {
            throw new InvalidCursorException("The cursor payload is not valid JSON.");
        }

        if (payload?.CreatedAt is null
            || !DateTimeOffset.TryParseExact(
                payload.CreatedAt, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var createdAt))
        {
            throw new InvalidCursorException("The cursor payload has no valid timestamp.");
        }

        if (payload.Id is null || !Guid.TryParse(payload.Id, out var id))
        {
            throw new InvalidCursorException("The cursor payload has no valid identifier.");
        }

        // Npgsql only accepts DateTimeOffset with Offset == 0 for timestamptz;
        // normalizing here keeps a crafted offset (or a locally-parsed value)
        // from becoming a 500 — a non-UTC cursor must stay a §13.12 400 or a
        // valid instant, never an unhandled error (M3T3 review S1; same fix the
        // M2T5 CursorPager codec applies).
        return (createdAt.ToUniversalTime(), id);
    }

    /// <summary>base64url without padding (URL- and query-safe).</summary>
    private static string ToUrlSafeBase64(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromUrlSafeBase64(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
        return Convert.FromBase64String(base64);
    }

    /// <summary>Wire payload — members pinned so the format survives refactors.</summary>
    private sealed class Payload
    {
        [JsonPropertyName("c")]
        public string? CreatedAt { get; set; }

        [JsonPropertyName("i")]
        public string? Id { get; set; }
    }
}

/// <summary>
/// Raised by <see cref="Cursor.Decode"/> for any strict-parse failure; the
/// admin endpoints map it to a §13.12 400 <c>/errors/validation</c> document.
/// </summary>
public sealed class InvalidCursorException(string message) : Exception(message);
