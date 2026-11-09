using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmailAgent.Spa.Auth;

namespace EmailAgent.Spa.Actions;

/// <summary>M4T4: typed client for /v1/user/actions (mirrors SourcesClient patterns).</summary>
public sealed class ActionsApiException : Exception
{
    public ActionsApiException(HttpStatusCode status, ApiProblem? problem, string message) : base(message)
    {
        Status = status;
        Problem = problem;
    }

    public HttpStatusCode Status { get; }
    public ApiProblem? Problem { get; }
    public IReadOnlyList<ApiProblemEntry> FieldErrors => Problem?.Errors ?? Array.Empty<ApiProblemEntry>();

    public static async Task<ActionsApiException> CreateAsync(HttpResponseMessage response)
    {
        string raw;
        try { raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false); }
        catch (IOException) { raw = string.Empty; }

        ApiProblem? problem = null;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try { problem = JsonSerializer.Deserialize<ApiProblem>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
            catch (JsonException) { }
        }

        var message = problem is null ? string.Empty : (problem.Detail ?? problem.Title);
        if (problem?.Errors is { Length: > 0 })
        {
            var fields = string.Join(" ", problem.Errors.Select(e => $"{e.Name}: {e.Reason}"));
            message = string.IsNullOrWhiteSpace(message) ? fields : $"{message} {fields}";
        }
        if (string.IsNullOrWhiteSpace(message))
            message = $"Request failed with status {(int)response.StatusCode} ({response.StatusCode}).";
        return new ActionsApiException(response.StatusCode, problem, message);
    }
}

public sealed class ActionsClient
{
    private readonly HttpClient _api;
    public ActionsClient(HttpClient api) => _api = api ?? throw new ArgumentNullException(nameof(api));

    public async Task<ActionListResponse> ListAsync(string? cursor, int limit = 25, string? typeId = null)
    {
        var url = $"/v1/user/actions?limit={limit}";
        if (!string.IsNullOrEmpty(cursor)) url += $"&cursor={Uri.EscapeDataString(cursor)}";
        if (!string.IsNullOrEmpty(typeId)) url += $"&filter[type_id]={Uri.EscapeDataString(typeId)}";
        using var response = await _api.GetAsync(url).ConfigureAwait(false);
        return await ReadAsync<ActionListResponse>(response).ConfigureAwait(false);
    }

    public async Task<ActionConfigDto> GetAsync(Guid id)
    {
        using var response = await _api.GetAsync($"/v1/user/actions/{id}").ConfigureAwait(false);
        return await ReadAsync<ActionConfigDto>(response).ConfigureAwait(false);
    }

    public async Task<ActionConfigDto> CreateAsync(CreateActionRequest request)
    {
        using var response = await _api.PostAsJsonAsync("/v1/user/actions", request).ConfigureAwait(false);
        return await ReadAsync<ActionConfigDto>(response).ConfigureAwait(false);
    }

    public async Task<ActionConfigDto> UpdateAsync(Guid id, UpdateActionRequest request)
    {
        using var response = await _api.PatchAsJsonAsync($"/v1/user/actions/{id}", request).ConfigureAwait(false);
        return await ReadAsync<ActionConfigDto>(response).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id)
    {
        using var response = await _api.DeleteAsync($"/v1/user/actions/{id}").ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await ActionsApiException.CreateAsync(response).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw await ActionsApiException.CreateAsync(response).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<T>().ConfigureAwait(false);
        return body ?? throw new ActionsApiException(response.StatusCode, null, "The server returned an unexpected response.");
    }
}

public sealed class ActionListResponse
{
    [JsonPropertyName("items")] public List<ActionConfigDto> Items { get; init; } = new();
    [JsonPropertyName("next_cursor")] public string? NextCursor { get; init; }
}

public sealed class ActionConfigDto
{
    [JsonPropertyName("id")] public Guid Id { get; init; }
    [JsonPropertyName("type_id")] public string TypeId { get; init; } = string.Empty;
    [JsonPropertyName("display_name")] public string DisplayName { get; init; } = string.Empty;
    [JsonPropertyName("require_confirmation")] public bool RequireConfirmation { get; init; }
    [JsonPropertyName("dry_run_default")] public bool DryRunDefault { get; init; }
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("config")] public JsonElement Config { get; init; }
}

public sealed class CreateActionRequest
{
    [JsonPropertyName("type_id")] public string TypeId { get; init; } = "imap-reply";
    [JsonPropertyName("display_name")] public string DisplayName { get; init; } = string.Empty;
    [JsonPropertyName("require_confirmation")] public bool RequireConfirmation { get; init; } = true;
    [JsonPropertyName("dry_run_default")] public bool DryRunDefault { get; init; }
    [JsonPropertyName("config")] public object Config { get; init; } = new Dictionary<string, object?>();
}

public sealed class UpdateActionRequest
{
    [JsonPropertyName("display_name")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? DisplayName { get; init; }
    [JsonPropertyName("require_confirmation")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? RequireConfirmation { get; init; }
    [JsonPropertyName("dry_run_default")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? DryRunDefault { get; init; }
    [JsonPropertyName("config")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public object? Config { get; init; }
}
