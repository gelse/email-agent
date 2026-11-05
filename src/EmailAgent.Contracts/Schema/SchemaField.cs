using System.Text.Json.Serialization;

namespace EmailAgent.Contracts.Schema;

/// <summary>
/// A single field extracted from a JSON Schema, describing one instance member.
/// </summary>
/// <remarks>
/// <see cref="Name"/> is a dotted path for nested members (<c>tls.min_version</c>),
/// while <see cref="ValidationIssue.Path"/> is the JSON Pointer form of the
/// same location (<c>/tls/min_version</c>) — consumers mapping one to the
/// other must unescape <c>~0</c>/<c>~1</c> segments and may split on
/// <c>.</c> (a literal <c>.</c> in a member name is therefore ambiguous in
/// <see cref="Name"/>).
/// </remarks>
public sealed record SchemaField
{
    /// <summary>Field name — the member key at the top level, a dotted path for nested fields.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>JSON type of the field, e.g. <c>string</c> or <c>integer</c>; <c>any</c> when the schema declares no type.</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// Whether the field is required by its containing object's schema — for
    /// nested fields, required within the parent object, which may itself be
    /// optional.
    /// </summary>
    [JsonPropertyName("required")]
    public required bool Required { get; init; }

    /// <summary>Documentation description, when the schema provides one.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }
}
