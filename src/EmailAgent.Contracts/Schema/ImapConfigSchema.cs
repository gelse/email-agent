using Json.Schema;

namespace EmailAgent.Contracts.Schema;

/// <summary>
/// The single source of truth for the IMAP plugin's <c>ConfigSchema</c> (M2T2
/// step 4, Q-F4, SQ-1). M2T4's <c>ImapMessageSource.ConfigSchema</c> returns
/// <c>ImapConfigSchema.Build()</c> so the loader (M2T3), the API validation
/// (M2T5) and the SPA mirror (M2T6) all see the same document.
/// </summary>
/// <remarks>
/// The schema is strict: <c>additionalProperties: false</c> rejects unknown
/// keys. A future plugin that needs extension keys must declare
/// <c>additionalProperties: true</c> explicitly in its own schema.
/// <c>default</c> values (993, true, INBOX) are annotations for consumers —
/// the runtime applies its own defaults (<c>port ?? 993</c>) regardless, so
/// behaviour matches whether or not the SPA form pre-fills them.
/// The built schema is cached: JsonSchema.Net registers every schema it
/// builds in a process-wide registry, and M2T4's
/// <c>ConfigSchema =&gt; ImapConfigSchema.Build()</c> reads that property
/// repeatedly — rebuilding would grow the registry on every access. The
/// instance is immutable and safe to evaluate concurrently.
/// </remarks>
public static class ImapConfigSchema
{
    private static readonly JsonSchema Schema = BuildCore();

    /// <summary>
    /// Builds the IMAP plugin's config schema (draft 2020-12); returns the
    /// cached instance built once per process.
    /// </summary>
    public static JsonSchema Build() => Schema;

    private static JsonSchema BuildCore() =>
        new JsonSchemaBuilder()
            .Schema("https://json-schema.org/draft/2020-12/schema")
            .Title("IMAP message source config")
            .Type(SchemaValueType.Object)
            .AdditionalProperties(false)
            .Properties(
                ("host", new JsonSchemaBuilder()
                    .Type(SchemaValueType.String)
                    .MinLength(1)
                    .Description("IMAP server hostname")),
                ("port", new JsonSchemaBuilder()
                    .Type(SchemaValueType.Integer)
                    .Minimum(1)
                    .Maximum(65535)
                    .Default(993)
                    .Description("IMAP port (993 for IMAPS)")),
                ("use_ssl", new JsonSchemaBuilder()
                    .Type(SchemaValueType.Boolean)
                    .Default(true)
                    .Description("Use TLS (recommended)")),
                ("username", new JsonSchemaBuilder()
                    .Type(SchemaValueType.String)
                    .MinLength(1)
                    .Description("IMAP username")),
                ("password", new JsonSchemaBuilder()
                    .Type(SchemaValueType.String)
                    .MinLength(1)
                    .Description("IMAP password (DEK-encrypted at rest)")),
                ("mailbox", new JsonSchemaBuilder()
                    .Type(SchemaValueType.String)
                    .MinLength(1)
                    .Default("INBOX")
                    .Description("Mailbox name")))
            .Required("host", "port", "use_ssl", "username", "password", "mailbox")
            .Build();
}
