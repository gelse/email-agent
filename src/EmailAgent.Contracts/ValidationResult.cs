using System.Text.Json.Serialization;

namespace EmailAgent.Contracts;

/// <summary>
/// The outcome of a config/args validation (§9; M2 SQ-1). Wire form:
/// <c>{"ok":false,"error":"...","issues":[{"path":"...","reason":"..."}]}</c>
/// (M2T2 asserts <c>issues[].path</c> / <c>issues[].reason</c>).
/// </summary>
public record ValidationResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] string? Error = null,
    [property: JsonPropertyName("issues")] IReadOnlyList<ValidationIssue>? Issues = null)
{
    /// <summary>A passing result with no error and no issues.</summary>
    public static ValidationResult Success() => new(true);

    /// <summary>A failing result; <paramref name="issues"/> carries field-level detail when available.</summary>
    public static ValidationResult Failure(
        string error, IReadOnlyList<ValidationIssue>? issues = null) =>
        new(false, error, issues);
}
