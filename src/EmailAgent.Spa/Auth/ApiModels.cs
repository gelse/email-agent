using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Json;

namespace EmailAgent.Spa.Auth;

/// <summary>
/// M1T8: the SPA's mirror of the published wire contracts (§13.13 snake_case
/// fields). The API's own response records live in <c>EmailAgent.Api</c>,
/// which the WASM bundle must not reference — these DTOs pin the same
/// member names with explicit <c>[JsonPropertyName]</c>, so a wire change
/// breaks compilation/tests on both sides. Request bodies share the shapes.
/// </summary>
public sealed class TokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    [JsonPropertyName("must_complete_first_login")]
    public bool? MustCompleteFirstLogin { get; init; }
}

/// <summary>RFC 7807 problem document (§13.12) as rendered by the SPA error boxes.</summary>
public sealed class ApiProblem
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("status")]
    public int? Status { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    [JsonPropertyName("errors")]
    public ApiProblemEntry[]? Errors { get; init; }
}

/// <summary>One <c>{ name, reason }</c> field-level entry of the problem document's <c>errors</c> array.</summary>
public sealed class ApiProblemEntry
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>Cursor page of <c>GET /v1/admin/users</c> (§13.13).</summary>
public sealed class PagedResponse<T>
{
    [JsonPropertyName("items")]
    public List<T> Items { get; init; } = new();

    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; init; }
}

/// <summary>The seven-member admin user projection — mirrors the API's <c>UserSummaryResponse</c>.</summary>
public sealed class UserSummary
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("must_change_password")]
    public bool MustChangePassword { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("last_login_at")]
    public DateTimeOffset? LastLoginAt { get; init; }
}

/// <summary>201 body of <c>POST /v1/admin/users</c> — the OTP appears exactly once (Q-A3).</summary>
public sealed class CreateUserResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("otp")]
    public string Otp { get; init; } = string.Empty;

    [JsonPropertyName("otp_expires_at")]
    public DateTimeOffset OtpExpiresAt { get; init; }
}

/// <summary>200 body of <c>POST /v1/admin/users/{id}/otp</c>.</summary>
public sealed class OtpResponse
{
    [JsonPropertyName("otp")]
    public string Otp { get; init; } = string.Empty;

    [JsonPropertyName("otp_expires_at")]
    public DateTimeOffset OtpExpiresAt { get; init; }
}

/// <summary>Body of <c>POST /v1/admin/users</c>.</summary>
public sealed class CreateUserRequest
{
    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; init; } = "User";
}

/// <summary>
/// Reads any non-success response as a human-readable message: the RFC 7807
/// <c>detail</c> plus per-field <c>errors</c> when present, or a generic
/// status line when the body is not a problem document.
/// </summary>
public static class ProblemReader
{
    public static async Task<string> ReadAsync(HttpResponseMessage response)
    {
        string? raw = null;
        try
        {
            raw = await response.Content.ReadAsStringAsync();
        }
        catch (IOException)
        {
            // fall through to the generic message
        }

        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                var problem = JsonSerializer.Deserialize<ApiProblem>(
                    raw, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (problem is not null)
                {
                    var message = problem.Detail ?? problem.Title;
                    if (problem.Errors is { Length: > 0 })
                    {
                        var fields = string.Join(
                            " ", problem.Errors.Select(e => $"{e.Name}: {e.Reason}"));
                        message = string.IsNullOrWhiteSpace(message) ? fields : $"{message} {fields}";
                    }

                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        return message;
                    }
                }
            }
            catch (JsonException)
            {
                // non-problem body — fall through
            }
        }

        return $"Request failed with status {(int)response.StatusCode} ({response.StatusCode}).";
    }

    /// <summary>POSTs a JSON body and returns the raw response (no exception on 4xx).</summary>
    public static Task<HttpResponseMessage> PostJsonAsync<T>(
        HttpClient client, string path, T body, CancellationToken ct = default)
        => client.PostAsJsonAsync(path, body, ct);
}
