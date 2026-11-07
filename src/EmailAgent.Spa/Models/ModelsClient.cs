using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EmailAgent.Spa.Auth;

namespace EmailAgent.Spa.Models;

// ---------------------------------------------------------------------------
// M3T5: the SPA's typed client for the six /v1/admin/models routes — M3T3's
// CRUD five plus M3T6's test-completion call (task scope bullets). The
// transport is the named "Api" HttpClient, so BearerTokenHandler supplies the
// JWT; nothing here repeats it.
//
// The DTOs below mirror the published wire shapes (§13.13 / §13.12
// snake_case) with explicit [JsonPropertyName] members, exactly like
// Sources/SourcesClient.cs — the WASM bundle must not reference EmailAgent.Api
// and a wire change should break both sides. ModelDto carries NO api_key: the
// API's projection never returns a credential, so neither does this one, and
// the password field of EditModelDialog starts blank on edit for the same
// reason (task Risks "PATCH semantics on api_key").
// ---------------------------------------------------------------------------

/// <summary>
/// A non-success response from the models API, carrying the parsed RFC 7807
/// document so the dialogs can render <c>errors[]</c> inline next to each
/// field and the probe-failed <c>detail</c> in the red banner (task step 3).
/// </summary>
public sealed class ModelsApiException : Exception
{
    public ModelsApiException(HttpStatusCode status, ApiProblem? problem, string message)
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
    /// The message a screen shows when it has no field to blame: the problem
    /// <c>detail</c>, prefixed with a retry hint on 429 so a rate-limited
    /// admin is told what to do instead of reading a bare status (task
    /// invariant: 429 stays friendly and inline).
    /// </summary>
    public string FriendlyMessage => Status == HttpStatusCode.TooManyRequests
        ? "Too many requests — wait a moment and try again. " + Message
        : Message;

    /// <summary>
    /// Reads the body once: the RFC 7807 document when it parses as one, plus
    /// the same human-readable message <see cref="ProblemReader"/> builds.
    /// </summary>
    public static async Task<ModelsApiException> CreateAsync(HttpResponseMessage response)
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

        return new ModelsApiException(response.StatusCode, problem, message);
    }
}

/// <summary>
/// M3T5: <c>GET /v1/admin/models</c> … <c>{id}/test-completion</c> — one
/// method per endpoint the task's Service bullet lists. Every method throws
/// <see cref="ModelsApiException"/> on a non-success status (problem document
/// attached) so a dialog can render field errors inline, and lets transport
/// failures surface as <see cref="HttpRequestException"/> for the caller's
/// "could not reach the API" message.
/// </summary>
public sealed class ModelsClient
{
    private readonly HttpClient _api;

    public ModelsClient(HttpClient api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    /// <summary>
    /// <c>GET /v1/admin/models</c> — one keyset page (§13.13) with the three
    /// bracketed filters the endpoint binds. Values and keys are escaped here
    /// so a display name with <c>&amp;</c> or a space cannot break the query.
    /// </summary>
    public async Task<ModelListResponse> ListAsync(
        string? cursor,
        int limit = 25,
        bool? enabledFilter = null,
        string? providerFilter = null,
        string? displayNameContains = null)
    {
        var parts = new List<string> { Query("limit", limit.ToString()) };
        if (!string.IsNullOrEmpty(cursor))
        {
            parts.Add(Query("cursor", cursor));
        }

        // Keys are escaped too ("filter[enabled]" → "filter%5Benabled%5D"):
        // the query then round-trips through Uri unchanged and reads back as
        // the bracketed key the endpoint binds.
        if (enabledFilter is { } enabled)
        {
            parts.Add(Query("filter[enabled]", enabled ? "true" : "false"));
        }

        if (!string.IsNullOrEmpty(providerFilter))
        {
            parts.Add(Query("filter[provider]", providerFilter));
        }

        if (!string.IsNullOrEmpty(displayNameContains))
        {
            parts.Add(Query("filter[display_name_contains]", displayNameContains));
        }

        using var response = await _api.GetAsync(
            "/v1/admin/models?" + string.Join("&", parts)).ConfigureAwait(false);
        return await ReadAsync<ModelListResponse>(response).ConfigureAwait(false);
    }

    /// <summary>One escaped <c>key=value</c> query pair.</summary>
    private static string Query(string key, string value)
        => $"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";

    /// <summary><c>GET /v1/admin/models/{id}</c> — edit-mode pre-fill (never carries a key).</summary>
    public async Task<ModelDto> GetAsync(Guid id)
    {
        using var response = await _api.GetAsync($"/v1/admin/models/{id}").ConfigureAwait(false);
        return await ReadAsync<ModelDto>(response).ConfigureAwait(false);
    }

    /// <summary><c>POST /v1/admin/models</c> → 201 with the created, key-free projection (probe included).</summary>
    public async Task<ModelDto> CreateAsync(CreateModelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await _api.PostAsJsonAsync("/v1/admin/models", request)
            .ConfigureAwait(false);
        return await ReadAsync<ModelDto>(response).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>PATCH /v1/admin/models/{id}</c> — every member is optional on the
    /// wire and every member this DTO omits stays unchanged server-side. The
    /// dialog sends <c>display_name</c>, <c>provider</c>, <c>base_url</c>,
    /// <c>model_name</c> and <c>enabled</c> always, the prices when set, and
    /// <c>api_key</c> ONLY when the password field is non-empty: a blank
    /// field is absent from the body, so the stored credential is kept (task
    /// Risks "PATCH semantics on api_key").
    /// </summary>
    public async Task<ModelDto> UpdateAsync(Guid id, UpdateModelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await _api.PatchAsJsonAsync($"/v1/admin/models/{id}", request)
            .ConfigureAwait(false);
        return await ReadAsync<ModelDto>(response).ConfigureAwait(false);
    }

    /// <summary><c>DELETE /v1/admin/models/{id}</c> → 204.</summary>
    public async Task DeleteAsync(Guid id)
    {
        using var response = await _api.DeleteAsync($"/v1/admin/models/{id}").ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await ModelsApiException.CreateAsync(response).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <c>POST /v1/admin/models/{id}/test-completion</c> → the §13.12 200
    /// projection: response text, tool calls, tokens, derived cost and
    /// duration. A refused call (unknown model, disabled model, bad shape,
    /// provider failure) arrives as <see cref="ModelsApiException"/>.
    /// </summary>
    public async Task<TestCompletionResponse> TestCompletionAsync(
        Guid id, TestCompletionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await _api.PostAsJsonAsync(
            $"/v1/admin/models/{id}/test-completion", request).ConfigureAwait(false);
        return await ReadAsync<TestCompletionResponse>(response).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await ModelsApiException.CreateAsync(response).ConfigureAwait(false);
        }

        var body = await response.Content.ReadFromJsonAsync<T>().ConfigureAwait(false);
        return body ?? throw new ModelsApiException(
            response.StatusCode, null, "The server returned an unexpected response.");
    }
}

// ---------------------------------------------------------------------------
// Wire DTOs (§13.13 / §13.12) — mirrors of EmailAgent.Api's
// AdminModelsDtos and TestCompletionDtos.
// ---------------------------------------------------------------------------

/// <summary>One keyset page of <c>GET /v1/admin/models</c>.</summary>
public sealed class ModelListResponse
{
    [JsonPropertyName("items")]
    public List<ModelDto> Items { get; init; } = new();

    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; init; }
}

/// <summary>
/// A catalog entry as the API returns it — the fixed 12-member projection,
/// never an <c>api_key</c>, an <c>api_key_secret_ref</c> or a blob id.
/// </summary>
public sealed class ModelDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>e.g., <c>openai-compatible</c> (read-only in M3 — SQ-M3-14).</summary>
    [JsonPropertyName("provider")]
    public string Provider { get; init; } = string.Empty;

    [JsonPropertyName("base_url")]
    public string BaseUrl { get; init; } = string.Empty;

    [JsonPropertyName("model_name")]
    public string ModelName { get; init; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    /// <summary>Null until the entry has been probed.</summary>
    [JsonPropertyName("tool_calling_probed_at")]
    public DateTimeOffset? ToolCallingProbedAt { get; init; }

    /// <summary><c>Pass</c> / <c>Fail</c>, or null for "never probed".</summary>
    [JsonPropertyName("tool_calling_probe_result")]
    public string? ToolCallingProbeResult { get; init; }

    [JsonPropertyName("created_by")]
    public Guid CreatedBy { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("price_per_1k_input_usd")]
    public decimal? PricePer1kInputUsd { get; init; }

    [JsonPropertyName("price_per_1k_output_usd")]
    public decimal? PricePer1kOutputUsd { get; init; }
}

/// <summary>Body of <c>POST /v1/admin/models</c>.</summary>
public sealed class CreateModelRequest
{
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("base_url")]
    public string? BaseUrl { get; init; }

    [JsonPropertyName("model_name")]
    public string? ModelName { get; init; }

    /// <summary>The credential — sent once, stored only as a wrapped blob (Q-E7).</summary>
    [JsonPropertyName("api_key")]
    public string? ApiKey { get; init; }

    [JsonPropertyName("price_per_1k_input_usd")]
    public decimal? PricePer1kInputUsd { get; init; }

    [JsonPropertyName("price_per_1k_output_usd")]
    public decimal? PricePer1kOutputUsd { get; init; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }
}

/// <summary>
/// Body of <c>PATCH /v1/admin/models/{id}</c> — every member optional and a
/// <c>null</c> member omitted from the body entirely, so "unchanged" is
/// expressible as "not sent" (task Risks: a blank <c>api_key</c> keeps the
/// stored one).
/// </summary>
public sealed class UpdateModelRequest
{
    [JsonPropertyName("display_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; init; }

    [JsonPropertyName("provider")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Provider { get; init; }

    [JsonPropertyName("base_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaseUrl { get; init; }

    [JsonPropertyName("model_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelName { get; init; }

    /// <summary>Supplying a key re-wraps it into a new blob; omitting it keeps the stored one.</summary>
    [JsonPropertyName("api_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiKey { get; init; }

    [JsonPropertyName("price_per_1k_input_usd")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? PricePer1kInputUsd { get; init; }

    [JsonPropertyName("price_per_1k_output_usd")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? PricePer1kOutputUsd { get; init; }

    [JsonPropertyName("enabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Enabled { get; init; }
}

/// <summary>Body of <c>POST /v1/admin/models/{id}/test-completion</c>.</summary>
public sealed class TestCompletionRequest
{
    /// <summary>Optional — null or empty means "no system prompt".</summary>
    [JsonPropertyName("system_prompt")]
    public string? SystemPrompt { get; init; }

    [JsonPropertyName("user_prompt")]
    public string? UserPrompt { get; init; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolSpec>? Tools { get; init; }
}

/// <summary>One tool declaration: name, optional description and the JSON-Schema parameters document.</summary>
public sealed class ToolSpec
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("parameters")]
    public JsonNode? Parameters { get; init; }
}

/// <summary>One model-requested tool call in the test-completion response.</summary>
public sealed class ToolCall
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("arguments")]
    public JsonElement Arguments { get; init; }
}

/// <summary>
/// 200 body of the test-completion endpoint (§13.12). Prompt/completion
/// tokens are nullable on purpose: a provider that reports no usage surfaces
/// JSON nulls, which the dialog renders as <c>—</c>.
/// </summary>
public sealed class TestCompletionResponse
{
    [JsonPropertyName("response_text")]
    public string ResponseText { get; init; } = string.Empty;

    [JsonPropertyName("tool_calls")]
    public List<ToolCall>? ToolCalls { get; init; }

    [JsonPropertyName("prompt_tokens")]
    public int? PromptTokens { get; init; }

    [JsonPropertyName("completion_tokens")]
    public int? CompletionTokens { get; init; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; init; }

    [JsonPropertyName("cost_usd")]
    public decimal? CostUsd { get; init; }

    [JsonPropertyName("duration_ms")]
    public int DurationMs { get; init; }
}
