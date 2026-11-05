using System.Text.Json.Serialization;

namespace EmailAgent.Contracts;

/// <summary>
/// A field-level validation error inside a <see cref="ValidationResult"/>
/// (§9; M2 SQ-1) — JSON <c>{"path":"password","reason":"required"}</c>.
/// </summary>
public record ValidationIssue(
    [property: JsonPropertyName("path")]
    [property: JsonRequired]
    string Path,
    [property: JsonPropertyName("reason")]
    [property: JsonRequired]
    string Reason);
