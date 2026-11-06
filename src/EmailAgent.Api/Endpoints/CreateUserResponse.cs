using System.Text.Json.Serialization;
using EmailAgent.Infrastructure.Enums;

namespace EmailAgent.Api.Endpoints;

/// <summary>
/// 201 body of <c>POST /v1/admin/users</c> (M1T7 task step 7). The raw OTP
/// is present exactly once in the response — Q-A3 view-once: it is never
/// persisted, never logged, and no later call can return it again (only a
/// fresh reissue via <c>POST /v1/admin/users/{id}/otp</c> yields another).
/// </summary>
public sealed record CreateUserResponse
{
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    [JsonPropertyName("email")]
    public required string Email { get; init; }

    [JsonPropertyName("role")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required UserRole Role { get; init; }

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required UserStatus Status { get; init; }

    /// <summary>The raw 16-char base32 OTP (Q-A2) — returned exactly once.</summary>
    [JsonPropertyName("otp")]
    public required string Otp { get; init; }

    [JsonPropertyName("otp_expires_at")]
    public required DateTimeOffset OtpExpiresAt { get; init; }
}
