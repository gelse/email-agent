namespace EmailAgent.Contracts.ActionHandlers;

/// <summary>
/// M4: the shared side-effect <see cref="SideEffect.Kind"/> vocabulary that
/// handlers emit and the dispatcher matches on (SQ-M4-4 / M4T1). Using named
/// constants avoids per-handler string drift.
/// </summary>
public static class SideEffectKinds
{
    /// <summary>A dry-run: describes the email that would be sent.</summary>
    public const string WouldSendEmail = "would_send_email";

    /// <summary>An executed send: the email was handed to the SMTP server.</summary>
    public const string SentEmail = "sent_email";
}
