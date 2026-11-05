using System.Globalization;

namespace EmailAgent.Api.Pagination;

/// <summary>
/// §13.13 <c>limit</c> parsing: default 25, maximum 100, values outside
/// 1..100 are validation failures (§13.12 400). Kept here — not inside the
/// endpoint — so the rule is unit-testable without a host
/// (<c>Pagination_LimitOverMax_Returns400</c>); the endpoint maps a rejected
/// value to an RFC 7807 400 <c>errors</c> entry named <c>limit</c>.
/// </summary>
public static class PaginationQuery
{
    /// <summary>§13.13 default page size.</summary>
    public const int DefaultLimit = 25;

    /// <summary>§13.13 maximum page size.</summary>
    public const int MaxLimit = 100;

    /// <summary>Minimum page size (a zero/negative page cannot be requested).</summary>
    public const int MinLimit = 1;

    /// <summary>
    /// Parses a raw <c>limit</c> query value. A null/empty value (parameter
    /// absent) yields <see cref="DefaultLimit"/>; anything outside
    /// <see cref="MinLimit"/>..<see cref="MaxLimit"/> is rejected with a
    /// human-readable reason for the §13.12 <c>errors</c> array.
    /// </summary>
    public static bool TryParseLimit(string? raw, out int limit, out string? error)
    {
        error = null;

        if (string.IsNullOrEmpty(raw))
        {
            limit = DefaultLimit;
            return true;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            limit = 0;
            error = $"limit must be an integer between {MinLimit} and {MaxLimit}.";
            return false;
        }

        if (parsed < MinLimit || parsed > MaxLimit)
        {
            limit = 0;
            error = $"limit must be between {MinLimit} and {MaxLimit}.";
            return false;
        }

        limit = parsed;
        return true;
    }
}