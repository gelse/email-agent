using System.Text.Json;

namespace EmailAgent.Connectors.Imap;

/// <summary>
/// M4T2: strongly typed SMTP action configuration (Q-C4). All seven fields
/// are manual user entries — never read from any <c>message_sources</c> row.
/// Port defaults are derived from <see cref="UseSsl"/> when <c>port</c> is 0:
/// 465 for implicit TLS, 587 otherwise.
/// </summary>
internal sealed record SmtpConfig(
    string Host,
    int Port,
    bool UseSsl,
    string Username,
    string Password,
    string FromAddress,
    string? FromName)
{
    /// <exception cref="JsonException">Missing/mistyped required members.</exception>
    public static SmtpConfig FromJson(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"SMTP configuration must be a JSON object, was {el.ValueKind}.");
        }

        var port = 0;
        if (el.TryGetProperty("port", out var portElement))
        {
            if (portElement.ValueKind != JsonValueKind.Number || !portElement.TryGetInt32(out var parsedPort))
            {
                throw new JsonException("SMTP configuration member 'port' must be an integer.");
            }

            port = parsedPort;
        }

        var useSsl = false;
        if (el.TryGetProperty("use_ssl", out var sslElement))
        {
            if (sslElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new JsonException("SMTP configuration member 'use_ssl' must be a boolean.");
            }

            useSsl = sslElement.GetBoolean();
        }

        string? fromName = null;
        if (el.TryGetProperty("from_name", out var nameElement) && nameElement.ValueKind != JsonValueKind.Null)
        {
            if (nameElement.ValueKind != JsonValueKind.String)
            {
                throw new JsonException("SMTP configuration member 'from_name' must be a string.");
            }

            fromName = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(fromName))
            {
                fromName = null;
            }
        }

        return WithDefaults(new SmtpConfig(
            RequiredString(el, "host"),
            port,
            useSsl,
            RequiredString(el, "username"),
            RequiredString(el, "password"),
            RequiredString(el, "from_address"),
            fromName));
    }

    public static SmtpConfig WithDefaults(SmtpConfig c) =>
        c with { Port = c.Port == 0 ? (c.UseSsl ? 465 : 587) : c.Port };

    private static string RequiredString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var value))
        {
            throw new JsonException($"SMTP configuration member '{name}' is required.");
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"SMTP configuration member '{name}' must be a string.");
        }

        return value.GetString() ?? string.Empty;
    }

    /// <summary>Password is never rendered (credentials must not reach logs/errors).</summary>
    public override string ToString() =>
        $"SmtpConfig {{ Host = {Host}, Port = {Port}, UseSsl = {UseSsl}, Username = {Username}, Password = ***, FromAddress = {FromAddress}, FromName = {FromName} }}";
}
