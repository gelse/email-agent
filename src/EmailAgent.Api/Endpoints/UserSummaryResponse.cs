using System.Text.Json.Serialization;
using EmailAgent.Infrastructure.Enums;

namespace EmailAgent.Api.Endpoints;

/// <summary>
/// The user projection returned by <c>GET /v1/admin/users</c> and
/// <c>GET /v1/admin/users/{id}</c> (M1T7 task step 3): exactly the seven
/// non-sensitive members — never <c>password_hash</c>, <c>kdf_salt</c>,
/// <c>kdf_params</c>, <c>dek_wrapped</c> or <c>dek_nonce</c>. Because the
/// record has no such members, including key material is a compile error;
/// <c>UserSummaryProjection_DoesNotExposeDekColumns</c> pins the exact member
/// set so an accidental addition fails the unit suite loudly.
/// </summary>
public sealed record UserSummaryResponse
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

    [JsonPropertyName("must_change_password")]
    public required bool MustChangePassword { get; init; }

    [JsonPropertyName("created_at")]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("last_login_at")]
    public DateTimeOffset? LastLoginAt { get; init; }
}
