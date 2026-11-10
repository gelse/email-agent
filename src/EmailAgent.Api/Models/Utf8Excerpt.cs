using System.Text;

namespace EmailAgent.Api.Models;

/// <summary>
/// UTF-8-safe byte-budget truncation shared by M3T2 (<c>ResponseExcerpt</c>)
/// and M3T6 (<c>SanitizeDetail</c>) — SQ-M3-3's 256-byte probe excerpt is a
/// BYTE budget, and a naive substring would be able to split a multi-byte
/// character and emit a replacement character into the audit row / RFC 7807
/// detail.
/// </summary>
public static class Utf8Excerpt
{
    /// <summary>
    /// Returns <paramref name="value"/> truncated to at most
    /// <paramref name="maxBytes"/> UTF-8 bytes. A value whose encoding already
    /// fits passes through unchanged; otherwise the cut index walks back off
    /// every continuation byte (<c>0b10xxxxxx</c>) to the nearest character
    /// boundary, so a multi-byte sequence is never split and no replacement
    /// character is ever produced.
    /// </summary>
    /// <param name="value">The string to clamp; must not be null.</param>
    /// <param name="maxBytes">The UTF-8 byte budget (values &lt;= 0 yield an empty string).</param>
    public static string Truncate(string value, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (maxBytes <= 0)
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maxBytes)
        {
            return value;
        }

        var count = maxBytes;
        while (count > 0 && (bytes[count] & 0xC0) == 0x80)
        {
            count--;
        }

        return Encoding.UTF8.GetString(bytes, 0, count);
    }
}
