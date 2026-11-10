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
/// M4T2 (Q-C4, Q-L7): <c>imap-reply</c> action — replies to the originating
/// message (threading headers derived from <see cref="ActionContext.SourceMessage"/>,
/// never from model args, SQ-M4-4). Dry-run produces a would-send description
/// without opening an SMTP session. SMTP credentials are manual per-handler
/// config (no reuse of any IMAP source).
/// </summary>
public sealed class ImapReplyActionHandler(ISmtpClientFactory smtpFactory) : IActionHandler
{
    public ImapReplyActionHandler() : this(new MailKitSmtpClientFactory()) { }

    /// <inheritdoc />
    public string TypeId => "imap-reply";

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
        return Task.FromResult(JsonSchemaValidator.Validate(ArgsSchemaInstance, args));
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

        var body = args.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String
            ? bodyEl.GetString() ?? string.Empty
            : string.Empty;
        string? subjectOverride = null;
        if (args.TryGetProperty("subject", out var subjEl) && subjEl.ValueKind == JsonValueKind.String)
        {
            subjectOverride = subjEl.GetString();
        }

        var toAddress = !string.IsNullOrWhiteSpace(ctx.SourceMessage.ReplyTo)
            ? ctx.SourceMessage.ReplyTo!
            : ctx.SourceMessage.From;
        if (string.IsNullOrWhiteSpace(toAddress))
        {
            return new ActionResult(false, "cannot parse original sender address");
        }

        if (!MailboxAddress.TryParse(toAddress, out var toMailbox))
        {
            return new ActionResult(false, "cannot parse original sender address");
        }

        var subject = ResolveSubject(subjectOverride, ctx.SourceMessage.Subject, reply: true);

        if (ctx.DryRun)
        {
            var desc = BuildDescription(smtp, ctx.SourceMessage, toAddress, subject, body);
            return new ActionResult(true, null,
                [new SideEffect(SideEffectKinds.WouldSendEmail, toAddress, desc)]);
        }

        try
        {
            var message = BuildMessage(smtp, toMailbox, subject, body, ctx.SourceMessage);
            using var client = smtpFactory.Create();
            var secure = smtp.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
            await client.ConnectAsync(smtp.Host, smtp.Port, secure, ct).ConfigureAwait(false);
            await client.AuthenticateAsync(smtp.Username, smtp.Password, ct).ConfigureAwait(false);
            await client.SendAsync(message, ct).ConfigureAwait(false);
            await client.DisconnectAsync(true, ct).ConfigureAwait(false);

            var sentDesc = BuildDescription(smtp, ctx.SourceMessage, toAddress, subject, body);
            return new ActionResult(true, null,
                [new SideEffect(SideEffectKinds.SentEmail, toAddress, sentDesc)]);
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

    private static MimeMessage BuildMessage(
        SmtpConfig smtp, MailboxAddress to, string subject, string body, SourceMessageSnapshot source)
    {
        var message = new MimeMessage();
        message.From.Add(!string.IsNullOrWhiteSpace(smtp.FromName)
            ? new MailboxAddress(smtp.FromName, smtp.FromAddress)
            : MailboxAddress.Parse(smtp.FromAddress));

        message.To.Add(to);
        message.Subject = subject;

        if (!string.IsNullOrWhiteSpace(source.MessageId))
        {
            message.InReplyTo = source.MessageId!;
            // RFC 5322 threading: References = prior References + Message-Id.
            var refs = (source.References ?? string.Empty).Trim();
            message.References.Add(refs.Length == 0 ? source.MessageId! : $"{refs} {source.MessageId}");
        }

        message.Body = new TextPart("plain") { Text = body };
        return message;
    }

    internal static string ResolveSubject(string? overrideSubject, string? originalSubject, bool reply)
    {
        if (!string.IsNullOrWhiteSpace(overrideSubject))
        {
            return overrideSubject!;
        }

        var original = originalSubject ?? string.Empty;
        if (original.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
            || original.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase))
        {
            return original;
        }

        var prefix = reply ? "Re: " : "Fwd: ";
        return prefix + original;
    }

    private static string BuildDescription(
        SmtpConfig smtp, SourceMessageSnapshot source, string to, string subject, string body)
    {
        var obj = new JsonObject
        {
            ["kind"] = "email_send",
            ["from"] = smtp.FromAddress,
            ["to"] = to,
            ["subject"] = subject,
            ["in_reply_to"] = source.MessageId,
            ["references"] = source.References,
            ["body_text"] = body,
            ["smtp"] = new JsonObject
            {
                ["host"] = smtp.Host,
                ["port"] = smtp.Port,
                ["use_ssl"] = smtp.UseSsl,
            },
        };
        // Deliberately no smtp password / username.
        return obj.ToJsonString();
    }

    private static JsonSchema BuildArgsSchema() =>
        new JsonSchemaBuilder()
            .Schema("https://json-schema.org/draft/2020-12/schema")
            .Type(SchemaValueType.Object)
            .AdditionalProperties(false)
            .Properties(
                ("body", new JsonSchemaBuilder()
                    .Type(SchemaValueType.String)
                    .MinLength(1)
                    .MaxLength(100_000)
                    .Description("Reply body text")),
                ("subject", new JsonSchemaBuilder()
                    .Type(SchemaValueType.String)
                    .MinLength(1)
                    .MaxLength(998)
                    .Description("Optional subject override")))
            .Required("body")
            .Build();
}
