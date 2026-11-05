using System.Text.Json.Serialization;

namespace EmailAgent.Api.Pagination;

/// <summary>
/// §13.13 (Q-API7) list envelope: the items array plus <c>next_cursor</c>,
/// which is <b>absent on the last page</b> (serialized only when non-null).
/// Member names are pinned snake_case — the published wire contract.
/// </summary>
public sealed record PagedResult<T>
{
    /// <summary>The page's items in <c>(created_at DESC, id DESC)</c> order.</summary>
    [JsonPropertyName("items")]
    public required IReadOnlyList<T> Items { get; init; }

    /// <summary>Cursor of the next page; omitted when this is the last page.</summary>
    [JsonPropertyName("next_cursor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextCursor { get; init; }
}