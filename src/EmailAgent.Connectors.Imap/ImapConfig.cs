using System.Text;
using System.Text.Json;

namespace EmailAgent.Connectors.Imap;

/// <summary>
/// Strongly typed IMAP connector configuration (M2T4). The JSON member names are
/// exactly the ones M2T2's <c>ImapConfigSchema</c> declares — that schema stays the
/// single source of truth for the wire shape (Q-F4 / SQ-1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Division of labour:</b> <see cref="ImapMessageSource.ValidateConfigAsync"/>
/// runs <c>JsonSchemaValidator</c> against <c>ConfigSchema</c> FIRST, so a
/// schema-invalid document never opens a socket (the <c>IMessageSource</c> contract
/// and M2T5's test-connection endpoint). <see cref="FromJson"/> therefore only has to
/// turn an already-schema-valid document into typed values — and, when it is called
/// directly (unit tests, future callers), to fail loudly on a missing or mistyped
/// required member with a <see cref="JsonException"/> instead of the
/// <c>KeyNotFoundException</c> a bare <c>GetProperty</c> would throw.
/// </para>
/// <para>
/// <b>Defaults</b> (plan §Steps 2): <c>port</c> falls back to 993 and <c>mailbox</c>
/// to <c>INBOX</c>; <c>port: 0</c> and an empty <c>mailbox</c> are normalised by
/// <see cref="WithDefaults"/>. The schema itself requires all six members and clamps
/// <c>port</c> to 1–65535, so the lenient path is reachable only through a direct
/// <see cref="FromJson"/> call.
/// </para>
/// </remarks>
internal sealed record ImapConfig(
    string Host,
    int Port,
    bool UseSsl,
    string Username,
    string Password,
    string Mailbox)
{
    /// <summary>
    /// Parses a configuration document.
    /// </summary>
    /// <exception cref="JsonException">
    /// The document is not an object, a required member (<c>host</c>,
    /// <c>username</c>, <c>password</c>) is missing, or a present member has the
    /// wrong JSON type.
    /// </exception>
    public static ImapConfig FromJson(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(
                $"IMAP configuration must be a JSON object, was {el.ValueKind}.");
        }

        var port = 993;
        if (el.TryGetProperty("port", out var portElement))
        {
            if (portElement.ValueKind != JsonValueKind.Number
                || !portElement.TryGetInt32(out var parsedPort))
            {
                throw new JsonException("IMAP configuration member 'port' must be an integer.");
            }

            port = parsedPort;
        }

        var useSsl = true;
        if (el.TryGetProperty("use_ssl", out var sslElement))
        {
            if (sslElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new JsonException("IMAP configuration member 'use_ssl' must be a boolean.");
            }

            useSsl = sslElement.GetBoolean();
        }

        var mailbox = "INBOX";
        if (el.TryGetProperty("mailbox", out var mailboxElement))
        {
            if (mailboxElement.ValueKind != JsonValueKind.String)
            {
                throw new JsonException("IMAP configuration member 'mailbox' must be a string.");
            }

            mailbox = mailboxElement.GetString() ?? "INBOX";
        }

        return WithDefaults(new ImapConfig(
            RequiredString(el, "host"),
            port,
            useSsl,
            RequiredString(el, "username"),
            RequiredString(el, "password"),
            mailbox));
    }

    /// <summary>
    /// Plan §Steps 2: normalises the two members that carry schema defaults
    /// (<c>port: 0</c> → 993, empty <c>mailbox</c> → <c>INBOX</c>).
    /// </summary>
    public static ImapConfig WithDefaults(ImapConfig c) =>
        c with
        {
            Port = c.Port == 0 ? 993 : c.Port,
            Mailbox = string.IsNullOrEmpty(c.Mailbox) ? "INBOX" : c.Mailbox,
        };

    /// <summary>Reads a required string member, throwing <see cref="JsonException"/> when it is missing or mistyped.</summary>
    private static string RequiredString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var value))
        {
            throw new JsonException($"IMAP configuration member '{name}' is required.");
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"IMAP configuration member '{name}' must be a string.");
        }

        return value.GetString() ?? string.Empty;
    }

    /// <summary>
    /// Replaces the record's compiler-generated <c>ToString()</c>, which would print
    /// <c>Password</c> — a credential must never reach a log line or an exception
    /// message once M5/M7 add either (review finding F7).
    /// </summary>
    public override string ToString() =>
        $"ImapConfig {{ Host = {Host}, Port = {Port}, UseSsl = {UseSsl}, "
        + $"Username = {Username}, Password = ***, Mailbox = {Mailbox} }}";
}
