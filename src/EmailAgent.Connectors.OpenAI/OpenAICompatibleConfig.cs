using System.Text.Json;

namespace EmailAgent.Connectors.OpenAI;

/// <summary>
/// Strongly typed OpenAI-compatible engine configuration (M3T1 step 3). The
/// JSON member names are exactly the ones M3T2's
/// <see cref="EmailAgent.Contracts.Schema.OpenAICompatibleConfigSchema"/>
/// declares — that schema stays the single source of truth for the wire shape.
/// </summary>
/// <remarks>
/// <para>
/// <b>Division of labour:</b> <see cref="OpenAICompatibleAgentEngine.ValidateConfigAsync"/>
/// runs <c>JsonSchemaValidator</c> against <c>ConfigSchema</c> FIRST, so a
/// schema-invalid document never reaches <see cref="FromJson"/>. When
/// <see cref="FromJson"/> is called directly (engine call path, unit tests), it
/// fails loudly with a <see cref="JsonException"/> on a missing, mistyped or
/// non-http(s) member instead of the <c>KeyNotFoundException</c> a bare
/// <c>GetProperty</c> would throw.
/// </para>
/// <para>
/// <b>Secrets:</b> <c>api_key</c> is a credential — <see cref="ToString"/>
/// redacts it so it can never reach a log line or exception message (the M2T4
/// <c>ImapConfig</c> review finding F7 precedent). The engine never caches the
/// key across calls: every <c>CompleteAsync</c> re-reads it from the call's
/// <c>AgentContext.EngineConfigJson</c>.
/// </para>
/// </remarks>
internal sealed record OpenAICompatibleConfig(
    string BaseUrl,
    string ApiKey,
    string ModelName)
{
    /// <summary>
    /// Parses a configuration document.
    /// </summary>
    /// <exception cref="JsonException">
    /// The document is not an object, a required member is missing or mistyped,
    /// or <c>base_url</c> is not an absolute http(s) URL.
    /// </exception>
    public static OpenAICompatibleConfig FromJson(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(
                $"OpenAI-compatible configuration must be a JSON object, was {el.ValueKind}.");
        }

        return Normalize(new OpenAICompatibleConfig(
            RequiredString(el, "base_url"),
            RequiredString(el, "api_key"),
            RequiredString(el, "model_name")));
    }

    /// <summary>
    /// Validates the one member that carries a format constraint the schema's
    /// runtime gate cannot be relied on for: <c>base_url</c> must parse as an
    /// absolute <c>http://</c> or <c>https://</c> URL (M3T1 step 3; the
    /// <c>format: uri</c> annotation itself is not evaluated by JsonSchema.Net
    /// without a registered format registry).
    /// </summary>
    /// <exception cref="JsonException">The URL is relative or not http(s).</exception>
    public static OpenAICompatibleConfig Normalize(OpenAICompatibleConfig c)
    {
        if (!Uri.TryCreate(c.BaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new JsonException("base_url must be an absolute http(s) URL.");
        }

        return c;
    }

    /// <summary>Reads a required string member, throwing <see cref="JsonException"/> when it is missing or mistyped.</summary>
    private static string RequiredString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var value))
        {
            throw new JsonException($"OpenAI-compatible configuration member '{name}' is required.");
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"OpenAI-compatible configuration member '{name}' must be a string.");
        }

        return value.GetString() ?? string.Empty;
    }

    /// <summary>
    /// Replaces the record's compiler-generated <c>ToString()</c>, which would
    /// print <c>ApiKey</c> — a credential must never reach a log line or an
    /// exception message.
    /// </summary>
    public override string ToString() =>
        $"OpenAICompatibleConfig {{ BaseUrl = {BaseUrl}, ApiKey = ***, ModelName = {ModelName} }}";
}
