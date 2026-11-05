using System.Text.Json.Serialization;

namespace EmailAgent.Api.Endpoints;

/// <summary>
/// 200 body of <c>POST /v1/admin/users/{id}/otp</c> (M1T7 task step 8): the
/// reissued raw OTP plus its expiry. Q-A3 view-once — <c>RegenerateAsync</c>
/// has already revoked every prior unused OTP, and the raw value exists only
/// in this response.
/// </summary>
public sealed record OtpResponse
{
    /// <summary>The raw 16-char base32 OTP (Q-A2) — returned exactly once.</summary>
    [JsonPropertyName("otp")]
    public required string Otp { get; init; }

    [JsonPropertyName("otp_expires_at")]
    public required DateTimeOffset OtpExpiresAt { get; init; }
}