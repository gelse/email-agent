using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmailAgent.Spa.Auth;

namespace EmailAgent.Spa.Sources;

// ---------------------------------------------------------------------------
// M2T6: the SPA's typed client for the seven M2T5 /v1/user/sources routes —
// one method per endpoint (task scope bullet 5). The transport is the named
// "Api" HttpClient, so BearerTokenHandler supplies the JWT and
// DekHeaderHandler supplies the M2-only X-User-Dek header; neither is
// repeated here.
//
// The DTOs below mirror the published wire shapes (§13.13 snake_case) with
// explicit [JsonPropertyName] members, exactly like Auth/ApiModels.cs — the
// WASM bundle must not reference EmailAgent.Api, and a wire change should
// break both sides. `password` exists on NO response type: the API never
// returns it, so the projection cannot leak it (M2T5 acceptance criterion).
// ---------------------------------------------------------------------------

/// <summary>
/// A non-success response from the sources API, carrying the parsed RFC 7807
/// document so callers can render <c>errors[]</c> inline next to each field.
/// </summary>
public sealed class SourcesApiException : Exception
{
    public SourcesApiException(HttpStatusCode status, ApiProblem? problem, string message)
        : base(message)
    {
        Status = status;
        Problem = problem;
    }

    public HttpStatusCode Status { get; }

    public ApiProblem? Problem { get; }

    /// <summary>Field-level entries of the problem document (empty when the body was not a problem).</summary>
    public IReadOnlyList<ApiProblemEntry> FieldErrors
        => Problem?.Errors ?? Array.Empty<ApiProblemEntry>();

    /// <summary>
    /// Reads the body once: the RFC 7807 document when it parses as one, plus
    /// the same human-readable message <see cref="ProblemReader"/> builds.
    /// </summary>
    public static async Task<SourcesApiException> CreateAsync(HttpResponseMessage response)
    {
        string raw;
        try
        {
            raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            raw = string.Empty;
        }

        ApiProblem? problem = null;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                problem = JsonSerializer.Deserialize<ApiProblem>(
                    raw, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch (JsonException)
            {
                // non-problem body — the generic status line below stands in
            }
        }

        var message = problem is null
            ? string.Empty
            : problem.Detail ?? problem.Title;
        if (problem?.Errors is { Length: > 0 })
        {
            var fields = string.Join(
                " ", problem.Errors.Select(error => $"{error.Name}: {error.Reason}"));
            message = string.IsNullOrWhiteSpace(message) ? fields : $"{message} {fields}";
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            message = $"Request failed with status {(int)response.StatusCode} ({response.StatusCode}).";
        }

        return new SourcesApiException(response.StatusCode, problem, message);
    }
}

/// <summary>
/// M2T6: <c>GET /v1/user/sources</c> … <c>fetch-now</c> — one method per
/// M2T5 endpoint. Every method throws <see cref="SourcesApiException"/> on a
/// non-success status (with the problem document attached) so the dialog can
/// render field errors inline, and lets transport failures surface as
/// <see cref="HttpRequestException"/> for the caller's "could not reach the
/// API" message.
/// </summary>
public sealed class SourcesClient(HttpClient api)
{


    /// <summary><c>GET /v1/user/sources</c> — one keyset page (§13.13).</summary>
    public async Task<SourceListResponse> ListAsync(string? cursor, int limit = 25)
    {
        var url = $"/v1/user/sources?limit={limit}";
        if (!string.IsNullOrEmpty(cursor))
        {
            url += $"&cursor={Uri.EscapeDataString(cursor)}";
        }

        using var response = await api.GetAsync(url).ConfigureAwait(false);
        return await ReadAsync<SourceListResponse>(response).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET /v1/user/sources/{id}</c> — edit-mode pre-fill. The projection
    /// carries no password, so the dialog's password field starts blank
    /// (task Risks "Password field UX on edit").
    /// </summary>
    public async Task<SourceDto> GetAsync(Guid id)
    {
        using var response = await api.GetAsync($"/v1/user/sources/{id}").ConfigureAwait(false);
        return await ReadAsync<SourceDto>(response).ConfigureAwait(false);
    }

    /// <summary><c>POST /v1/user/sources</c> → 201 with the created projection.</summary>
    public async Task<SourceDto> CreateAsync(CreateSourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await api.PostAsJsonAsync("/v1/user/sources", request)
            .ConfigureAwait(false);
        return await ReadAsync<SourceDto>(response).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>PATCH /v1/user/sources/{id}</c> — every member is optional on the
    /// wire. The dialog always sends <c>display_name</c>,
    /// <c>interval_minutes</c> and <c>config</c>; the config carries whatever
    /// the password field held, and an empty string or an absent member both
    /// mean "keep the stored one" (task Risks; server
    /// <c>SourceService.MergeStoredPassword</c> — M2T5's
    /// <c>PasswordEmptyString</c> test pins that).
    /// </summary>
    public async Task<SourceDto> UpdateAsync(Guid id, UpdateSourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await api.PatchAsJsonAsync($"/v1/user/sources/{id}", request)
            .ConfigureAwait(false);
        return await ReadAsync<SourceDto>(response).ConfigureAwait(false);
    }

    /// <summary><c>DELETE /v1/user/sources/{id}</c> → 204.</summary>
    public async Task DeleteAsync(Guid id)
    {
        using var response = await api.DeleteAsync($"/v1/user/sources/{id}").ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await SourcesApiException.CreateAsync(response).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <c>POST /v1/user/sources/test-connection</c> — always HTTP 200 with
    /// <c>{ ok, error }</c> for plugin outcomes (a refused login lives in the
    /// body), while an unknown plugin or a schema violation is a 400 problem
    /// that arrives as <see cref="SourcesApiException"/>.
    /// </summary>
    public async Task<TestConnectionResponse> TestConnectionAsync(TestConnectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await api.PostAsJsonAsync("/v1/user/sources/test-connection", request)
            .ConfigureAwait(false);
        return await ReadAsync<TestConnectionResponse>(response).ConfigureAwait(false);
    }

    /// <summary><c>POST /v1/user/sources/{id}/fetch-now</c> → the counts plus the advanced checkpoint.</summary>
    public async Task<FetchNowResponse> FetchNowAsync(Guid id)
    {
        using var response = await api.PostAsync(
            $"/v1/user/sources/{id}/fetch-now", new ByteArrayContent(Array.Empty<byte>()))
            .ConfigureAwait(false);
        return await ReadAsync<FetchNowResponse>(response).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await SourcesApiException.CreateAsync(response).ConfigureAwait(false);
        }

        var body = await response.Content.ReadFromJsonAsync<T>().ConfigureAwait(false);
        return body ?? throw new SourcesApiException(
            response.StatusCode, null, "The server returned an unexpected response.");
    }
}

// ---------------------------------------------------------------------------
// Wire DTOs (§13.13) — mirrors of EmailAgent.Api's SourcesDtos.
// ---------------------------------------------------------------------------

/// <summary>One keyset page of <c>GET /v1/user/sources</c>.</summary>
public sealed class SourceListResponse
{
    [JsonPropertyName("items")]
    public List<SourceDto> Items { get; init; } = new();

    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; init; }
}

/// <summary>A message source as the API returns it — never a password.</summary>
public sealed class SourceDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("type_id")]
    public string TypeId { get; init; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("interval_minutes")]
    public int IntervalMinutes { get; init; }

    [JsonPropertyName("checkpoint")]
    public SourceCheckpointDto Checkpoint { get; init; } = new();

    [JsonPropertyName("last_run_at")]
    public DateTimeOffset? LastRunAt { get; init; }

    [JsonPropertyName("next_run_at")]
    public DateTimeOffset? NextRunAt { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("config")]
    public SourceConfigDto Config { get; init; } = new();
}

/// <summary>The five IMAP members the projection exposes — the password is deliberately absent.</summary>
public sealed class SourceConfigDto
{
    [JsonPropertyName("host")]
    public string? Host { get; init; }

    [JsonPropertyName("port")]
    public int? Port { get; init; }

    [JsonPropertyName("use_ssl")]
    public bool? UseSsl { get; init; }

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonPropertyName("mailbox")]
    public string? Mailbox { get; init; }
}

/// <summary>The connector checkpoint projection (<c>last_uid</c> plus plugin extras).</summary>
public sealed class SourceCheckpointDto
{
    [JsonPropertyName("last_uid")]
    public string? LastUid { get; init; }

    [JsonPropertyName("extra")]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>Body of <c>POST /v1/user/sources</c>.</summary>
public sealed class CreateSourceRequest
{
    [JsonPropertyName("type_id")]
    public string TypeId { get; init; } = "imap";

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("interval_minutes")]
    public int IntervalMinutes { get; init; } = 5;

    /// <summary>The connector config document, password included when supplied.</summary>
    [JsonPropertyName("config")]
    public object Config { get; init; } = new Dictionary<string, object?>();
}

/// <summary>Body of <c>PATCH /v1/user/sources/{id}</c> — omitted members stay unchanged.</summary>
public sealed class UpdateSourceRequest
{
    [JsonPropertyName("display_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; init; }

    [JsonPropertyName("interval_minutes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? IntervalMinutes { get; init; }

    /// <summary>
    /// Absent (<c>null</c>) leaves the stored config untouched; a supplied
    /// document with an empty or missing <c>password</c> keeps the stored one.
    /// </summary>
    [JsonPropertyName("config")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Config { get; init; }
}

/// <summary>Body of <c>POST /v1/user/sources/test-connection</c>.</summary>
public sealed class TestConnectionRequest
{
    [JsonPropertyName("type_id")]
    public string TypeId { get; init; } = "imap";

    [JsonPropertyName("config")]
    public object Config { get; init; } = new Dictionary<string, object?>();
}

/// <summary>Always-200 body of <c>/test-connection</c>: plugin failures ride here, not in the status.</summary>
public sealed class TestConnectionResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>Body of <c>POST /v1/user/sources/{id}/fetch-now</c>.</summary>
public sealed class FetchNowResponse
{
    [JsonPropertyName("processed")]
    public int Processed { get; init; }

    [JsonPropertyName("new_processed_messages")]
    public int NewProcessedMessages { get; init; }

    [JsonPropertyName("checkpoint")]
    public SourceCheckpointDto Checkpoint { get; init; } = new();
}
