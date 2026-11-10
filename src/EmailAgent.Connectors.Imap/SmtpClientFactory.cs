using MailKit.Net.Smtp;

namespace EmailAgent.Connectors.Imap;

/// <summary>
/// M4T2: SMTP network seam (mirrors <see cref="IImapClientFactory"/>).
/// MailKit's <see cref="SmtpClient"/> is concrete, so tests substitute this
/// factory to assert the no-connection dry-run path.
/// </summary>
public interface ISmtpClientFactory
{
    /// <summary>Returns a NEW, unconnected client; the caller owns it.</summary>
    SmtpClient Create();
}

/// <summary>
/// Default factory: a plain MailKit <see cref="SmtpClient"/> over the system
/// trust store. No <c>#if DEBUG</c> trust-all escape hatch (production uses
/// the system trust store).
/// </summary>
public sealed class MailKitSmtpClientFactory : ISmtpClientFactory
{
    /// <inheritdoc />
    public SmtpClient Create() => new();
}
