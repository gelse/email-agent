using System.Text.Json.Serialization;

namespace EmailAgent.Api.Endpoints;

// ---------------------------------------------------------------------------
// M3T7 / SQ-M3-12: the /v1/user/models wire contract. The projection is a
// fixed FOUR-member list — { id, display_name, provider, model_name } — so no
// admin-only field can leak to an ordinary user: no base_url (it would reveal
// internal network topology), no prices, no created_by, and never an api_key
// or an encrypted-blob pointer. Member names are pinned with
// [JsonPropertyName] because every response serializes through the
// AOT-friendly source-gen context below, exactly like AdminModelsDtos.
// ---------------------------------------------------------------------------

/// <summary>A pickable model as the user namespace returns it (reduced on purpose).</summary>
public sealed class UserModelDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>e.g., <c>openai-compatible</c>.</summary>
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("model_name")]
    public string ModelName { get; set; } = string.Empty;
}

/// <summary>
/// Body of <c>GET /v1/user/models</c> — one capped page with no cursor: M3
/// ships the allow-list read without pagination, so the list is simply
/// <c>items</c> (§13.13 <c>limit</c> still applies: default 25, max 100).
/// </summary>
public sealed class UserModelListResponse
{
    [JsonPropertyName("items")]
    public List<UserModelDto> Items { get; set; } = new();
}

/// <summary>M3T7: the source-gen context every /v1/user/models response serializes through.</summary>
[JsonSerializable(typeof(UserModelListResponse))]
public partial class UserModelsJsonContext : JsonSerializerContext
{
}
