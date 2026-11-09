using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmailAgent.Contracts;
using EmailAgent.Contracts.ActionHandlers;
using EmailAgent.Contracts.Schema;
using Json.Schema;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace EmailAgent.Connectors.Imap;

/// <summary>
/// M4T2 (Q-C4, Q-L7): <c>imap-forward</c> action — forwards the originating
/// message to a model-chosen recipient with the original body appended plus an
/// optional note. A forward is a new message (it does not copy
/// <c>References</c>). Dry-run produces a would-send description without
/// opening an SMTP session.
/// </summary>
public sealed class ImapForwardActionHandler : IActionHandler
{
    private readonly ISmtpClientFactory _smtpFactory;

    public ImapForwardActionHandler() : this(new MailKitSmtpClientFactory()) { }

    public ImapForwardActionHandler(ISmtpClientFactory smtpFactory) => _smtpFactory = smtpFactory;

    /// <inheritdoc />
    public string TypeId => "imap-forward";

    private static readonly JsonSchema ConfigSchemaInstance = ImapActionConfigSchema.Build();
    private static readonly JsonSchema ArgsSchemaInstance = BuildArgsSchema();

    /// <inheritdoc />
    public JsonSchema ConfigSchema => ConfigSchemaInstance;

    /// <inheritdoc />
    public JsonSchema ArgsSchema => ArgsSchemaInstance;

    /// <inheritdoc />
    public Task<ValidationResult> ValidateConfigAsync(JsonElement config, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(JsonSchemaValidator.Validate(ConfigSchemaInstance, config));
    }

    /// <inheritdoc />
    public Task<ValidationResult> ValidateArgsAsync(JsonElement config, JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var schemaResult = JsonSchemaValidator.Validate(ArgsSchemaInstance, args);

        // Authoritative semantic check (M2T2: format is documentation-only;
        // pattern in the schema is a client mirror). Run TryParse so a
        // garbage address always yields the TryParse reason, even if the
        // schema also rejected it.
        if (args.TryGetProperty("to", out var toEl) && toEl.ValueKind == JsonValueKind.String)
        {
            var to = toEl.GetString();
            if (string.IsNullOrWhiteSpace(to)
                || !MailboxAddress.TryParse(to, out var parsed)
                || string.IsNullOrWhiteSpace(parsed.Address)
                || !parsed.Address.Contains('@', StringComparison.Ordinal))
            {
                return Task.FromResult(ValidationResult.Failure(
                    "schema validation failed",
                    [new ValidationIssue("to", "not a valid email address")]));
            }
        }

        return Task.FromResult(schemaResult);
    }

    /// <inheritdoc />
    public async Task<ActionResult> ExecuteAsync(
        JsonElement config, JsonElement args, ActionContext ctx, CancellationToken ct)
    {
        if (ctx.SourceMessage is null)
        {
            return new ActionResult(false, "no source message context");
        }

        SmtpConfig smtp;
        try
        {
            smtp = SmtpConfig.FromJson(config);
        }
        catch (JsonException ex)
        {
            return new ActionResult(false, $"smtp send failed: invalid config ({ex.Message})");
        }

        var to = args.TryGetProperty("to", out var toEl) && toEl.ValueKind == JsonValueKind.String
            ? toEl.GetString() ?? string.Empty
            : string.Empty;
        if (!MailboxAddress.TryParse(to, out var toMailbox))
        {
            return new ActionResult(false, $"smtp send failed: invalid recipient");
        }

        var note = args.TryGetProperty("note", out var noteEl) && noteEl.ValueKind == JsonValueKind.String
            ? noteEl.GetString()
            : null;
        var includeOriginal = !args.TryGetProperty("include_original_body", out var incEl)
            || incEl.ValueKind != JsonValueKind.False;
        string? subjectOverride = null;
        if (args.TryGetProperty("subject", out var subjEl) && subjEl.ValueKind == JsonValueKind.String)
        {
            subjectOverride = subjEl.GetString();
        }

        var subject = ImapReplyActionHandler.ResolveSubject(subjectOverride, ctx.SourceMessage.Subject, reply: false);
        var body = ComposeBody(note, includeOriginal, ctx.SourceMessage.BodyText);

        if (ctx.DryRun)
        {
            var desc = BuildDescription(smtp, to, subject, body, note, includeOriginal);
            return new ActionResult(true, null,
                [new SideEffect(SideEffectKinds.WouldSendEmail, to, desc)]);
        }

        try
        {
            var message = BuildMessage(smtp, toMailbox, subject, body);
            using var client = _smtpFactory.Create();
            var secure = smtp.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
            await client.ConnectAsync(smtp.Host, smtp.Port, secure, ct).ConfigureAwait(false);
            await client.AuthenticateAsync(smtp.Username, smtp.Password, ct).ConfigureAwait(false);
            await client.SendAsync(message, ct).ConfigureAwait(false);
            await client.DisconnectAsync(true, ct).ConfigureAwait(false);

            var sentDesc = BuildDescription(smtp, to, subject, body, note, includeOriginal);
            return new ActionResult(true, null,
                [new SideEffect(SideEffectKinds.SentEmail, to, sentDesc)]);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is AuthenticationException or SmtpCommandException or SmtpProtocolException
            or ServiceNotConnectedException or ServiceNotAuthenticatedException
            or SocketException or IOException)
        {
            return new ActionResult(false, $"smtp send failed: {ex.GetType().Name}");
        }
        catch (Exception ex)
        {
            return new ActionResult(false, $"smtp send failed: {ex.GetType().Name}");
        }
    }

    private static string ComposeBody(string? note, bool includeOriginal, string? originalBody)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrEmpty(note))
        {
            parts.Add(note);
        }

        if (includeOriginal)
        {
            if (parts.Count > 0)
            {
                parts.Add(string.Empty);
                parts.Add("---------- Forwarded message ----------");
            }

            parts.Add(originalBody ?? string.Empty);
        }

        return string.Join("\n", parts);
    }

    private static MimeMessage BuildMessage(SmtpConfig smtp, MailboxAddress to, string subject, string body)
    {
        var message = new MimeMessage();
        if (!string.IsNullOrWhiteSpace(smtp.FromName))
        {
            message.From.Add(new MailboxAddress(smtp.FromName, smtp.FromAddress));
        }
        else
        {
            message.From.Add(MailboxAddress.Parse(smtp.FromAddress));
        }

        message.To.Add(to);
        message.Subject = subject;
        // Forward is a new message — do not copy References.
        message.Body = new TextPart("plain") { Text = body };
        return message;
    }

    private static string BuildDescription(
        SmtpConfig smtp, string to, string subject, string body, string? note, bool includeOriginal)
    {
        var obj = new JsonObject
        {
            ["kind"] = "email_send",
            ["from"] = smtp.FromAddress,
            ["to"] = to,
            ["forward_to"] = to,
            ["subject"] = subject,
            ["body_text"] = body,
            ["note_included"] = !string.IsNullOrEmpty(note),
            ["original_body_included"] = includeOriginal,
            ["smtp"] = new JsonObject
            {
                ["host"] = smtp.Host,
                ["port"] = smtp.Port,
                ["use_ssl"] = smtp.UseSsl,
            },
        };
        return obj.ToJsonString();
    }

    private static JsonSchema BuildArgsSchema() =>
        new JsonSchemaBuilder()
            .Schema("https://json-schema.org/draft/2020-12/schema")
            .Type(SchemaValueType.Object)
            .AdditionalProperties(false)
            .Properties(
                ("to", new JsonSchemaBuilder()
                    .Type(SchemaValueType.String)
                    .MinLength(1)
                    .Format(Formats.Email)
                    .Pattern("^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$")
                    .Description("Recipient email address (authoritatively validated with MailboxAddress.TryParse)")),
                ("note", new JsonSchemaBuilder()
                    .Type(SchemaValueType.String)
                    .MaxLength(10_000)
                    .Description("Optional note prepended to the forwarded body")),
                ("subject", new JsonSchemaBuilder()
                    .Type(SchemaValueType.String)
                    .MinLength(1)
                    .MaxLength(998)
                    .Description("Optional subject override")),
                ("include_original_body", new JsonSchemaBuilder()
                    .Type(SchemaValueType.Boolean)
                    .Default(true)
                    .Description("Append the original body text (default true)")))
            .Required("to")
            .Build();
}
