namespace EmailAgent.Infrastructure.Identity;

/// <summary>
/// Binds the <c>Otp</c> configuration section (env <c>OTP__Lifetime</c>, §16):
/// the one-time-password lifetime decided by Q-A2 (7 days). Only
/// Infrastructure consumes it — M1T6/M1T7 call <c>IOtpService</c>, never the
/// options type — so it lives next to the service, like
/// <c>AuditOptions</c> (§5 layering).
/// </summary>
public sealed class OtpOptions
{
    /// <summary>Configuration section name (<c>OTP__*</c> env vars land here).</summary>
    public const string SectionName = "Otp";

    /// <summary>OTP lifetime (env <c>OTP__Lifetime</c>); default 7 days per Q-A2.</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromDays(7);
}
