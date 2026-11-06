using MailKit.Net.Imap;

namespace EmailAgent.Connectors.Imap;

/// <summary>
/// The connector's only network seam (M2T4 Risks "ImapClient is not mockable"):
/// MailKit's <see cref="ImapClient"/> is a concrete class, so unit tests substitute
/// this factory instead of faking the IMAP conversation.
/// </summary>
public interface IImapClientFactory
{
    /// <summary>
    /// Returns a NEW, unconnected client. The caller owns it and is responsible for
    /// connecting, authenticating, disconnecting and disposing it — there is no
    /// pooling (connection reuse is M5's job; M2T4 "Out").
    /// </summary>
    ImapClient Create();
}

/// <summary>
/// Default factory: a plain MailKit <see cref="ImapClient"/> over the SYSTEM trust
/// store. Deliberately no <c>ServerCertificateValidationCallback</c> trust-all hook
/// (M2T4 Risks "Greenmail cert pinning": production must use the system trust store,
/// and the code enforces that by having no <c>#if DEBUG</c> escape hatch at all) —
/// against a self-signed server this factory therefore yields a handshake failure,
/// which <see cref="ImapMessageSource"/> reports as a connection error.
/// </summary>
public sealed class MailKitImapClientFactory : IImapClientFactory
{
    /// <inheritdoc />
    public ImapClient Create() => new();
}
