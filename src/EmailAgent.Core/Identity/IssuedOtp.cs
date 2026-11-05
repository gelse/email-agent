namespace EmailAgent.Core.Identity;

/// <summary>
/// Result of issuing or regenerating a one-time password (Q-A2 / Q-A3):
/// the persisted row identity plus the raw 16-char base32 OTP, which the
/// API layer returns exactly once (M1T7 response / M1T8 view-once modal).
/// The raw value is never persisted — the database stores only the PBKDF2
/// <c>salt || hash</c> and can never yield it again.
/// </summary>
public sealed record IssuedOtp(Guid Id, Guid UserId, string RawOtp, DateTimeOffset ExpiresAt);
