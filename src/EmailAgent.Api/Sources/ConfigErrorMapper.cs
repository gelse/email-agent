using System.Text.Json;
using EmailAgent.Contracts;
using Json.Schema;

namespace EmailAgent.Api.Sources;

/// <summary>
/// M2T5: turns the plugin-schema issues of
/// <see cref="JsonSchemaValidator.Validate(JsonSchema, JsonElement)"/> into
/// the <c>errors: [{ name, reason }]</c> array of a §13.12
/// <c>/errors/config-validation</c> document.
/// </summary>
/// <remarks>
/// <para>
/// <c>name</c> is the failing member's wire name: the JSON-Pointer path
/// <see cref="ValidationIssue.Path"/> with pointer escapes decoded and
/// nesting joined by <c>.</c> (<c>/port</c> → <c>port</c>,
/// <c>/tls/min_version</c> → <c>tls.min_version</c>); a root-level issue is
/// attributed to <c>config</c>, the request member that carried the document.
/// </para>
/// <para>
/// <c>reason</c> is human-readable rather than the raw JSON-Schema keyword:
/// the keyword is resolved against the plugin's own schema document, so a
/// bound violation on <c>port</c> reads <c>must be between 1 and 65535</c>
/// (the milestone's pinned example) instead of <c>minimum</c>. A keyword the
/// schema does not declare (or a member it cannot resolve) falls back to the
/// keyword itself, so no issue is ever dropped.
/// </para>
/// </remarks>
public static class ConfigErrorMapper
{
    /// <summary>The wire name used for issues that blame the whole config document.</summary>
    public const string RootName = "config";

    /// <summary>Maps every issue exactly once, preserving the validator's order.</summary>
    public static IReadOnlyList<(string Name, string Reason)> Map(
        JsonSchema schema, IReadOnlyList<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(issues);

        var root = schema.Root.Source;
        var mapped = new List<(string Name, string Reason)>(issues.Count);

        foreach (var issue in issues)
        {
            var subschema = Resolve(root, issue.Path);
            mapped.Add((WireName(issue.Path), ReasonFor(subschema, issue.Reason)));
        }

        return mapped;
    }

    /// <summary>JSON Pointer → dotted wire name (<c>/port</c> → <c>port</c>).</summary>
    public static string WireName(string pointer)
    {
        if (string.IsNullOrEmpty(pointer) || pointer == "/")
        {
            return RootName;
        }

        var segments = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            segments[i] = segments[i].Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
        }

        return string.Join('.', segments);
    }

    /// <summary>
    /// The schema document that governs the instance member at
    /// <paramref name="pointer"/>; the root schema when the path does not
    /// resolve (a composition keyword or an out-of-schema member).
    /// </summary>
    private static JsonElement Resolve(JsonElement schemaDocument, string pointer)
    {
        var current = schemaDocument;
        foreach (var raw in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = raw.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);

            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty("properties", out var properties)
                || properties.ValueKind != JsonValueKind.Object
                || !properties.TryGetProperty(segment, out var child))
            {
                break;
            }

            current = child;
        }

        return current;
    }

    /// <summary>Keyword → human-readable reason, using the member's own bounds where they exist.</summary>
    private static string ReasonFor(JsonElement subschema, string keyword)
    {
        if (keyword is "minimum" or "maximum" or "exclusiveMinimum" or "exclusiveMaximum")
        {
            // The pinned contract shape: a bounded numeric member reports BOTH
            // bounds whichever side of them the instance crossed.
            if (TryBound(subschema, "minimum", out var minimum)
                && TryBound(subschema, "maximum", out var maximum))
            {
                return $"must be between {minimum} and {maximum}";
            }

            if (TryBound(subschema, keyword, out var bound))
            {
                return keyword.StartsWith("max", StringComparison.Ordinal)
                    ? $"must be at most {bound}"
                    : $"must be at least {bound}";
            }
        }

        switch (keyword)
        {
            case "required":
                return "required";
            case "type":
                return $"must be of type {TypeText(subschema)}";
            case "minLength":
                return Count(subschema, keyword) is { } min
                    ? $"must be at least {min} characters"
                    : keyword;
            case "maxLength":
                return Count(subschema, keyword) is { } max
                    ? $"must be at most {max} characters"
                    : keyword;
            case "minItems":
                return Count(subschema, keyword) is { } minItems
                    ? $"must contain at least {minItems} items"
                    : keyword;
            case "maxItems":
                return Count(subschema, keyword) is { } maxItems
                    ? $"must contain at most {maxItems} items"
                    : keyword;
            case "additionalProperties":
                return "is not an allowed property";
            case "enum":
                return EnumValues(subschema) is { Length: > 0 } values
                    ? $"must be one of: {string.Join(", ", values)}"
                    : keyword;
            case "const":
                return subschema.TryGetProperty("const", out var constant)
                    ? $"must be {constant.GetRawText()}"
                    : keyword;
            case "pattern":
                return subschema.TryGetProperty("pattern", out var pattern)
                    ? $"must match {pattern.GetString()}"
                    : keyword;
            case "format":
                return subschema.TryGetProperty("format", out var format)
                    ? $"must be a valid {format.GetString()}"
                    : keyword;
            case "false" or "":
                return "is not valid";
            default:
                // The keyword itself: the M2T2 contract names the failing
                // JSON-Schema keyword, and an unmapped one is still exact.
                return keyword;
        }
    }

    /// <summary>The declared <c>type</c> of the member, for the <c>type</c> keyword message.</summary>
    private static string TypeText(JsonElement subschema)
    {
        if (!subschema.TryGetProperty("type", out var type))
        {
            return "the declared type";
        }

        if (type.ValueKind == JsonValueKind.String)
        {
            return type.GetString() ?? "the declared type";
        }

        if (type.ValueKind == JsonValueKind.Array)
        {
            var union = string.Join(
                "|",
                type.EnumerateArray()
                    .Where(t => t.ValueKind == JsonValueKind.String)
                    .Select(t => t.GetString()));
            if (union.Length > 0)
            {
                return union;
            }
        }

        return "the declared type";
    }

    /// <summary>A numeric bound rendered exactly as the schema wrote it (<c>1</c>, not <c>1.0</c>).</summary>
    private static bool TryBound(JsonElement subschema, string keyword, out string bound)
    {
        bound = string.Empty;
        if (subschema.ValueKind != JsonValueKind.Object
            || !subschema.TryGetProperty(keyword, out var value)
            || value.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        bound = value.GetRawText();
        return true;
    }

    private static int? Count(JsonElement subschema, string keyword)
        => subschema.ValueKind == JsonValueKind.Object
            && subschema.TryGetProperty(keyword, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var count)
            ? count
            : null;

    private static string[] EnumValues(JsonElement subschema)
        => subschema.ValueKind == JsonValueKind.Object
            && subschema.TryGetProperty("enum", out var values)
            && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(v => v.GetRawText()).ToArray()
            : Array.Empty<string>();
}
