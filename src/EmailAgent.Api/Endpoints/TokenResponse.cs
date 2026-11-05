using System.Text.Json.Serialization;

namespace EmailAgent.Api.Endpoints;

/// <summary>
/// Response body of the token-issuing account endpoints (§13.3): login,
/// otp-login and refresh. The <c>snake_case</c> member names are the
/// published contract the M1T8 SPA consumes — pinned with explicit
/// <c>[JsonPropertyName]</c> so they survive any naming-policy change.
/// </summary>
public sealed record TokenResponse
{
    /// <summary>Short-lived HS256 bearer access token (SPA memory only, §13.10).</summary>
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }

    /// <summary>Access-token TTL in seconds (<c>AUTH__AccessTokenTtl</c>, default 900).</summary>
    [JsonPropertyName("expires_in")]
    public required int ExpiresIn { get; init; }

    /// <summary>
    /// <c>true</c> when the session must complete the first-login flow
    /// (<c>must_change_password = true</c> OR <c>dek_wrapped IS NULL</c>).
    /// Always present on login/otp-login; omitted on refresh (task step 8).
    /// </summary>
    [JsonPropertyName("must_complete_first_login")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? MustCompleteFirstLogin { get; init; }
}
