using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace EmailAgent.Spa.Models;

// ---------------------------------------------------------------------------
// M3T7 (SQ-M3-12): the SPA's typed client for GET /v1/user/models — the
// reduced allow-list the /user/agents/new picker is built from. The transport
// is the named "Api" HttpClient, so BearerTokenHandler supplies the JWT;
// nothing here repeats it.
//
// Non-success answers surface as the SAME ModelsApiException ModelsClient
// raises (one RFC 7807 reader for every models namespace call), so a page
// already handling /v1/admin/models failures needs no second error model.
//
// The DTOs below mirror the published four-member wire shape
// ({ id, display_name, provider, model_name }) with explicit
// [JsonPropertyName] members, exactly like Sources/SourcesClient.cs and
// ModelsClient.cs — the WASM bundle must not reference EmailAgent.Api, and a
// projection that ever grew an admin-only member must break both sides.
// ---------------------------------------------------------------------------

/// <summary>
/// <c>GET /v1/user/models</c> — one capped page of pickable models. The
/// server has already applied the §11 allow-list
/// (<c>enabled = true AND tool_calling_probe_result = Pass</c>): the SPA
/// renders what comes back and never filters for itself (task invariant).
/// </summary>
public sealed class UserModelsClient
{
    private readonly HttpClient _api;

    public UserModelsClient(HttpClient api)
        => _api = api ?? throw new ArgumentNullException(nameof(api));

    /// <summary>
    /// Loads the allow-listed models.
    /// </summary>
    /// <param name="limit">§13.13 page size — default 25, max 100 (the picker asks for 100).</param>
    /// <returns>The reduced DTO list, in the server's order.</returns>
    /// <exception cref="ModelsApiException">The API answered a non-success status.</exception>
    public async Task<IReadOnlyList<UserModelDto>> ListAsync(int limit = 100)
    {
        using var response = await _api.GetAsync(
            "/v1/user/models?limit=" + Uri.EscapeDataString(limit.ToString()))
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await ModelsApiException.CreateAsync(response).ConfigureAwait(false);
        }

        var body = await response.Content
            .ReadFromJsonAsync<UserModelListResponse>()
            .ConfigureAwait(false);
        return body?.Items ?? [];
    }
}

// ---------------------------------------------------------------------------
// Wire DTOs (§13.13) — the mirror of EmailAgent.Api's UserModelsDtos.
// ---------------------------------------------------------------------------

/// <summary>Body of <c>GET /v1/user/models</c> (no cursor in M3).</summary>
public sealed class UserModelListResponse
{
    [JsonPropertyName("items")]
    public List<UserModelDto> Items { get; init; } = new();
}

/// <summary>
/// A pickable model — the fixed four-member projection, never a
/// <c>base_url</c> (internal topology), never a price, never a
/// <c>created_by</c>, never an <c>api_key</c>.
/// </summary>
public sealed class UserModelDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>e.g., <c>openai-compatible</c>.</summary>
    [JsonPropertyName("provider")]
    public string Provider { get; init; } = string.Empty;

    [JsonPropertyName("model_name")]
    public string ModelName { get; init; } = string.Empty;
}
