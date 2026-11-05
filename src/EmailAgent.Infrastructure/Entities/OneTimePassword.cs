namespace EmailAgent.Infrastructure.Entities;

/// <summary>
/// §6 <c>one_time_passwords</c> row. <c>otp_hash</c> is PBKDF2-SHA256 as
/// <c>salt || hash</c> (16 + 32 = 48 bytes, Q-A8/SQ-5 — written by M1T5,
/// never here); <c>revoked_at</c> is set on regenerate/revoke-all
/// (SQ-6 / Q-A9).
/// </summary>
public class OneTimePassword
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public byte[] OtpHash { get; set; } = null!;

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set when the OTP is consumed (single-use, Q-A2).</summary>
    public DateTimeOffset? UsedAt { get; set; }

    /// <summary>Set when a newer OTP supersedes this unused one (SQ-6 / Q-A9).</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Admin who issued it. Soft reference — §6 does not label it FK.</summary>
    public Guid CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public User User { get; set; } = null!;
}