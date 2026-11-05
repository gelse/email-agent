namespace EmailAgent.Api.Endpoints;

/// <summary>
/// Form payload of <c>POST /v1/account/otp-login</c> (M1T6 task step 5 —
/// the Q-A2 one-time password exchanged for the first-login session).
/// </summary>
public sealed record OtpLoginRequest
{
    /// <summary>Account email.</summary>
    public string Email { get; init; } = string.Empty;

    /// <summary>The 16-char URL-safe base32 one-time password.</summary>
    public string Otp { get; init; } = string.Empty;
}
