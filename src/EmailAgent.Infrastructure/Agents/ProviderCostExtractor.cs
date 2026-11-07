using System.Globalization;
using System.Text.Json;

namespace EmailAgent.Infrastructure.Agents;

/// <summary>
/// M3T4 — readers for the provider-specific cost surfaces of the §11
/// cost-capture rule: the <c>x-litellm-cost</c> response header and the
/// <c>cost</c> field inside an OpenAI-compatible <c>usage</c> block. Pure
/// helpers; every failure mode (missing, wrong type, unparseable) degrades to
/// <c>null</c> so the caller can fall through to the next rule.
/// </summary>
public static class ProviderCostExtractor
{
    /// <summary>
    /// Reads a cost header (case-insensitive name match). Returns <c>null</c>
    /// when the dictionary is <c>null</c>, the header is absent/empty, or the
    /// value does not parse as a decimal. Accepted grammar is
    /// invariant-culture, no group separators: sign, digits, decimal point and
    /// exponent (<see cref="NumberStyles.Float"/>) — a locale-mangled
    /// <c>0,0007</c> degrades to <c>null</c> instead of parsing as 7.
    /// <para>
    /// One value per header name: when a header repeats, pass the single
    /// value (e.g. the last) — a comma-joined multi-value string does not
    /// parse and therefore degrades to <c>null</c>.
    /// </para>
    /// </summary>
    /// <param name="headers">Response headers; may be <c>null</c>.</param>
    /// <param name="name">Header name, e.g. <c>x-litellm-cost</c>. Generic so
    /// future provider surfaces reuse the same helper.</param>
    public static decimal? ExtractFromHeader(IReadOnlyDictionary<string, string>? headers, string name)
    {
        if (headers is null)
        {
            return null;
        }

        var match = headers.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(match.Value))
        {
            return null;
        }

        return decimal.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>
    /// Reads <c>usage.cost</c> if present (LiteLLM's cost formatter emits it
    /// as a JSON number or numeric string). Returns <c>null</c> for a
    /// non-object block, a missing <c>cost</c>, an unparseable string, a
    /// number that is not representable as <see cref="decimal"/> (out of
    /// range or too many significant digits — <c>TryGetDecimal</c>, never the
    /// throwing <c>GetDecimal</c>), or any other value kind.
    /// </summary>
    public static decimal? ExtractFromUsage(JsonElement? usage)
    {
        // NB: no `is not { ... } u` designation — C# forbids declaring a
        // variable inside a `not` pattern (CS8780); match, then lift.
        if (usage is not { ValueKind: JsonValueKind.Object })
        {
            return null;
        }

        var u = usage.Value;
        if (!u.TryGetProperty("cost", out var costEl))
        {
            return null;
        }

        if (costEl.ValueKind == JsonValueKind.Number)
        {
            return costEl.TryGetDecimal(out var numericCost) ? numericCost : null;
        }

        if (costEl.ValueKind == JsonValueKind.String)
        {
            return decimal.TryParse(
                costEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var textualCost)
                ? textualCost
                : null;
        }

        return null;
    }
}
