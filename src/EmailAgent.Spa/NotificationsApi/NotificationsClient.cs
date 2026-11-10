using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmailAgent.Spa.Auth;

namespace EmailAgent.Spa.NotificationsApi;

public sealed class NotificationsApiException(HttpStatusCode status, ApiProblem? problem, string message)
    : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public ApiProblem? Problem { get; } = problem;

    public static async Task<NotificationsApiException> CreateAsync(HttpResponseMessage response)
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
        var message = problem?.Detail ?? problem?.Title
            ?? $"Request failed with status {(int)response.StatusCode} ({response.StatusCode}).";
        return new NotificationsApiException(response.StatusCode, problem, message);
    }
}

public sealed class NotificationsClient(HttpClient api)
{
    private readonly HttpClient _api = api ?? throw new ArgumentNullException(nameof(api));

    public async Task<NotificationListResponse> ListAsync(string? cursor, int limit = 25, bool? unreadOnly = null)
    {
        var url = $"/v1/user/notifications?limit={limit}";
        if (!string.IsNullOrEmpty(cursor)) url += $"&cursor={Uri.EscapeDataString(cursor)}";
        if (unreadOnly.HasValue) url += $"&filter[unread]={(unreadOnly.Value ? "true" : "false")}";
        using var response = await _api.GetAsync(url).ConfigureAwait(false);
        return await ReadAsync<NotificationListResponse>(response).ConfigureAwait(false);
    }

    public async Task<NotificationDto> MarkReadAsync(Guid id)
    {
        using var response = await _api.PostAsync($"/v1/user/notifications/{id}/read", new ByteArrayContent(Array.Empty<byte>())).ConfigureAwait(false);
        return await ReadAsync<NotificationDto>(response).ConfigureAwait(false);
    }

    public async Task<ReadAllResponse> MarkAllReadAsync()
    {
        using var response = await _api.PostAsync("/v1/user/notifications/read-all", new ByteArrayContent(Array.Empty<byte>())).ConfigureAwait(false);
        return await ReadAsync<ReadAllResponse>(response).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw await NotificationsApiException.CreateAsync(response).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<T>().ConfigureAwait(false);
        return body ?? throw new NotificationsApiException(response.StatusCode, null, "The server returned an unexpected response.");
    }
}

public sealed class NotificationListResponse
{
    [JsonPropertyName("items")] public List<NotificationDto> Items { get; init; } = new();
    [JsonPropertyName("next_cursor")] public string? NextCursor { get; init; }
}

public sealed class NotificationDto
{
    [JsonPropertyName("id")] public Guid Id { get; init; }
    [JsonPropertyName("kind")] public string Kind { get; init; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
    [JsonPropertyName("body")] public string? Body { get; init; }
    [JsonPropertyName("related_type")] public string? RelatedType { get; init; }
    [JsonPropertyName("related_id")] public Guid? RelatedId { get; init; }
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("read_at")] public DateTimeOffset? ReadAt { get; init; }
}

public sealed record ReadAllResponse
{
    [JsonPropertyName("updated")] public int Updated { get; init; }
}
