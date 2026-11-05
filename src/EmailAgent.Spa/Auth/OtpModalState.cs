namespace EmailAgent.Spa.Auth;

/// <summary>
/// M1T8 (task step 11): view-once state for the admin OTP modal (Q-A3).
/// Scoped so it lives for the SPA session: once the operator closes the
/// modal for a given OTP value, a parent re-render — or a remount after
/// navigating away and back — with that same value renders nothing. The
/// server-side half of view-once is that the raw OTP exists only in the
/// create/reissue response body and is never re-fetchable.
/// </summary>
public sealed class OtpModalState
{
    private readonly HashSet<string> _dismissed = new(StringComparer.Ordinal);

    /// <summary>True when the operator already dismissed a modal showing <paramref name="otp"/>.</summary>
    public bool IsDismissed(string otp)
        => !string.IsNullOrEmpty(otp) && _dismissed.Contains(otp);

    /// <summary>Marks <paramref name="otp"/> as dismissed (Close button).</summary>
    public void Dismiss(string otp)
    {
        if (!string.IsNullOrEmpty(otp))
        {
            _dismissed.Add(otp);
        }
    }

    /// <summary>Forgets all dismissals (test reset only).</summary>
    public void Reset() => _dismissed.Clear();
}
