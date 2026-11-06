using Json.Schema;

namespace EmailAgent.Contracts.Schema;

/// <summary>
/// The single source of truth for the OpenAI-compatible engine plugin's
/// <c>ConfigSchema</c> (M3T1 step 4, mirroring M2T2's
/// <see cref="ImapConfigSchema"/>). The plugin's
/// <c>OpenAICompatibleAgentEngine.ConfigSchema</c> delegates to
/// <see cref="Build"/> so the loader (M2T3), the API boundary (M3T3's model
/// catalog) and the SPA introspector (M3T5) all see the same document.
/// </summary>
/// <remarks>
/// <para>
/// The schema carries only what the engine needs to make a call
/// (<c>base_url</c>, <c>api_key</c>, <c>model_name</c>). Catalog-level fields
/// (<c>display_name</c>, <c>provider</c>, price, <c>enabled</c>) live on
/// <c>model_catalog</c> / <c>POST /v1/admin/models</c>, never here (M3T1
/// Scope). <c>additionalProperties: false</c> rejects unknown keys.
/// </para>
/// <para>
/// The built schema is cached like <see cref="ImapConfigSchema"/>'s: JsonSchema.Net
/// registers every schema it builds in a process-wide registry, and the engine
/// reads <c>ConfigSchema</c> repeatedly — rebuilding would grow the registry on
/// every access. The instance is immutable and safe to evaluate concurrently.
/// </para>
/// </remarks>
public static class OpenAICompatibleConfigSchema
{
    private static readonly JsonSchema Schema = BuildCore();

    /// <summary>
    /// Builds the OpenAI-compatible engine's config schema (draft 2020-12);
    /// returns the cached instance built once per process.
    /// </summary>
    public static JsonSchema Build() => Schema;

    private static JsonSchema BuildCore() =>
        new JsonSchemaBuilder()
            .Schema("https://json-schema.org/draft/2020-12/schema")
            .Title("OpenAI-compatible engine config")
            .Type(SchemaValueType.Object)
            .AdditionalProperties(false)
            .Properties(
                ("base_url", new JsonSchemaBuilder()
                    .Type(SchemaValueType.String)
                    .Format("uri")
                    .Pattern("^https?://")
                    .MinLength(1)
                    .Description("OpenAI-compatible endpoint base URL")),
                ("api_key", new JsonSchemaBuilder()
                    .Type(SchemaValueType.String)
                    .MinLength(1)
                    .Description("API key (encrypted at rest under SYSTEM__KeyEncryptionKey per Q-E7)")),
                ("model_name", new JsonSchemaBuilder()
                    .Type(SchemaValueType.String)
                    .MinLength(1)
                    .Description("Model identifier (e.g., gpt-4o-mini, claude-3-5-sonnet)")))
            .Required("base_url", "api_key", "model_name")
            .Build();
}
