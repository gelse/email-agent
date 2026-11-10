using Json.Schema;

namespace EmailAgent.Contracts.Schema;

/// <summary>
/// M4T2 (Q-C4): the single source of truth for the shared SMTP config schema
/// used by <c>imap-reply</c> and <c>imap-forward</c>. Housed in Contracts (the
/// M2T2 pattern — like <see cref="ImapConfigSchema"/> /
/// <see cref="OpenAICompatibleConfigSchema"/>) so handlers and the SPA mirror
/// the same document without touching the plugin assembly. All values are
/// entered manually by the user; nothing is derived from any IMAP source.
/// </summary>
public static class ImapActionConfigSchema
{
    private static readonly JsonSchema Schema = BuildCore();

    /// <summary>Cached schema instance (immutable, safe to evaluate concurrently).</summary>
    public static JsonSchema Build() => Schema;

    private static JsonSchema BuildCore() =>
        new JsonSchemaBuilder()
            .Schema("https://json-schema.org/draft/2020-12/schema")
            .Title("IMAP action SMTP config")
            .Type(SchemaValueType.Object)
            .AdditionalProperties(false)
            .Properties(
                ("host", new JsonSchemaBuilder()
                    .Type(SchemaValueType.String)
                    .MinLength(1)
                    .Description("SMTP server hostname")),
                ("port", new JsonSchemaBuilder()
                    .Type(SchemaValueType.Integer)
                    .Minimum(1)
                    .Maximum(65535)
                    .Description("SMTP port (465 with SSL, 587 with STARTTLS by default)")),
                ("use_ssl", new JsonSchemaBuilder()
                    .Type(SchemaValueType.Boolean)
                    .Default(false)
                    .Description("Use implicit TLS (SSL-on-connect)")),
                ("username", new JsonSchemaBuilder()
                    .Type(SchemaValueType.String)
                    .MinLength(1)
                    .Description("SMTP username")),
                ("password", new JsonSchemaBuilder()
                    .Type(SchemaValueType.String)
                    .MinLength(1)
                    .Description("SMTP password (DEK-encrypted at rest)")),
                ("from_address", new JsonSchemaBuilder()
                    .Type(SchemaValueType.String)
                    .MinLength(1)
                    .Format(Formats.Email)
                    .Description("Sender email address")),
                ("from_name", new JsonSchemaBuilder()
                    .Type(SchemaValueType.String)
                    .Description("Sender display name (optional)")))
            .Required("host", "username", "password", "from_address")
            .Build();
}
