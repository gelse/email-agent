using System.Text.Json;
using System.Text.Json.Serialization;
using EmailAgent.Contracts.AgentEngines;

namespace EmailAgent.Connectors.OpenAI.Http;

/// <summary>
/// Translates an <see cref="AgentRequest"/> into the OpenAI-compatible
/// <c>POST /chat/completions</c> body (M3T1 step 6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Prompt convention (SQ-M3-9):</b> the caller assembles
/// <c>Prompt</c> as <c>"SYSTEM:\n{system}\n\nUSER:\n{user}"</c>; the builder
/// splits it on exactly those markers. A prompt that does not carry both
/// markers (the M3 test-completion path passing no system prompt, M5's worker
/// pool owning prompt assembly) is sent whole as a single user message —
/// lenient fallback on purpose, asserted by the unit tests.
/// </para>
/// <para>
/// <b>Tools (Q-L4):</b> one OpenAI <c>function</c> tool per entry of
/// <see cref="AgentRequest.AllowedActionSchemas"/>, emitted only when the
/// caller allowed at least one schema — with <c>tool_choice: "auto"</c>. The
/// mapping is a one-shot translation, not a schema rewrite: <c>name</c> is the
/// schema key (the <c>typeId</c>), <c>description</c> the schema's own
/// description annotation (empty string when absent), and <c>parameters</c> the
/// schema's <c>properties</c> object verbatim (an empty object when the schema
/// declares none — documented simplification of the full JSON-Schema →
/// function-parameters mapping, verified by the unit tests).
/// </para>
/// </remarks>
public static class OpenAIRequestBuilder
{
    private const string SystemMarker = "SYSTEM:\n";
    private const string UserMarker = "\n\nUSER:\n";

    /// <summary>Builds the serialized request body.</summary>
    /// <remarks>
    /// Streamed calls carry <c>stream_options: {"include_usage": true}</c> so
    /// the upstream still emits the trailing usage-only frame (the frame
    /// <see cref="OpenAIResponseMapper.MapFrame"/> maps onto the merged
    /// terminal <c>AgentDone</c>) — without it OpenAI omits <c>usage</c> from
    /// every streamed chunk. Non-streamed bodies omit the property entirely.
    /// A non-null <paramref name="maxTokens"/> adds <c>max_tokens: N</c> to
    /// the body (M3T2's probe transmits SQ-M3-3's canonical <c>max_tokens: 1</c>
    /// cost bound through <see cref="EngineCallOptions.MaxTokens"/> — Q-L15);
    /// <see langword="null"/> omits the member entirely, so every existing
    /// call keeps its exact request shape.
    /// </remarks>
    /// <param name="req">The agent request (prompt, allowed action schemas).</param>
    /// <param name="cfg">The engine configuration supplying <c>model_name</c>.</param>
    /// <param name="stream">The <c>stream</c> flag for this call.</param>
    /// <param name="maxTokens">The <c>max_tokens</c> cap for this call; <see langword="null"/> omits the field.</param>
    internal static string Build(AgentRequest req, OpenAICompatibleConfig cfg, bool stream, int? maxTokens = null)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(cfg);

        var messages = BuildMessages(req.Prompt);

        // The empty-parameters fallback must stay alive until the body below
        // serializes it (a JsonElement cannot outlive its JsonDocument).
        using var emptyProperties = JsonDocument.Parse("{}");
        var tools = BuildTools(req, emptyProperties.RootElement);

        var request = new ChatCompletionRequest(
            Model: cfg.ModelName,
            Messages: messages,
            Stream: stream,
            Tools: tools.Count > 0 ? tools : null,
            ToolChoice: tools.Count > 0 ? "auto" : null,
            StreamOptions: stream ? new StreamOptions(IncludeUsage: true) : null,
            MaxTokens: maxTokens);

        return JsonSerializer.Serialize(request, OpenAIJsonOptions.Default);
    }

    /// <summary>
    /// Splits the prompt on the SQ-M3-9 markers; a prompt without both of
    /// them becomes one user message carrying the whole text.
    /// </summary>
    private static IReadOnlyList<ChatMessage> BuildMessages(string prompt)
    {
        if (prompt.StartsWith(SystemMarker, StringComparison.Ordinal))
        {
            var userIndex = prompt.IndexOf(UserMarker, StringComparison.Ordinal);
            if (userIndex >= 0)
            {
                var system = prompt.Substring(SystemMarker.Length, userIndex - SystemMarker.Length);
                var user = prompt.Substring(userIndex + UserMarker.Length);
                return new[]
                {
                    new ChatMessage("system", system),
                    new ChatMessage("user", user),
                };
            }
        }

        return new[] { new ChatMessage("user", prompt) };
    }

    /// <summary>
    /// One OpenAI function tool per allowed action schema (empty when none
    /// were allowed); <paramref name="emptyProperties"/> is the document that
    /// backs the fallback <c>parameters</c> value — it must outlive the
    /// serialization in <see cref="Build"/>.
    /// </summary>
    private static IReadOnlyList<ChatFunctionTool> BuildTools(AgentRequest req, JsonElement emptyProperties)
    {
        if (req.AllowedActionSchemas.Count == 0)
        {
            return Array.Empty<ChatFunctionTool>();
        }

        var tools = new List<ChatFunctionTool>(req.AllowedActionSchemas.Count);

        foreach (var (typeId, schema) in req.AllowedActionSchemas)
        {
            var schemaJson = schema.Root.Source;
            var description = schemaJson.ValueKind == JsonValueKind.Object
                          && schemaJson.TryGetProperty("description", out var descriptionElement)
                          && descriptionElement.ValueKind == JsonValueKind.String
                ? descriptionElement.GetString() ?? string.Empty
                : string.Empty;

            var parameters = schemaJson.ValueKind == JsonValueKind.Object
                          && schemaJson.TryGetProperty("properties", out var properties)
                          && properties.ValueKind == JsonValueKind.Object
                ? properties
                : emptyProperties;

            tools.Add(new ChatFunctionTool(
                "function",
                new ChatFunction(typeId, description, parameters)));
        }

        return tools;
    }

    /// <summary>OpenAI chat message — <c>{"role":"...","content":"..."}</c>.</summary>
    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    /// <summary>OpenAI tool entry — <c>{"type":"function","function":{...}}</c>.</summary>
    private sealed record ChatFunctionTool(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("function")] ChatFunction Function);

    /// <summary>OpenAI function declaration — name, description and JSON-Schema parameters.</summary>
    private sealed record ChatFunction(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("parameters")] JsonElement Parameters);

    /// <summary>
    /// The canonical OpenAI-compatible request body. <c>tools</c> /
    /// <c>tool_choice</c> are omitted (not <c>null</c>-written) when the caller
    /// allowed no schemas, keeping the body exactly in the documented shape;
    /// <c>stream_options</c> is present only on streamed calls; and
    /// <c>max_tokens</c> is present only when the caller set
    /// <see cref="EngineCallOptions.MaxTokens"/>.
    /// </summary>
    private sealed record ChatCompletionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("tools")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<ChatFunctionTool>? Tools = null,
        [property: JsonPropertyName("tool_choice")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? ToolChoice = null,
        [property: JsonPropertyName("stream_options")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        StreamOptions? StreamOptions = null,
        [property: JsonPropertyName("max_tokens")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? MaxTokens = null);

    /// <summary>
    /// <c>stream_options</c> — <c>{"include_usage": true}</c> asks the
    /// upstream to append its usage-only frame before <c>[DONE]</c>.
    /// </summary>
    private sealed record StreamOptions(
        [property: JsonPropertyName("include_usage")] bool IncludeUsage);
}
