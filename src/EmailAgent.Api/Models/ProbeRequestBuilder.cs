using System.Text.Json;
using System.Text.Json.Serialization;
using EmailAgent.Contracts.AgentEngines;
using EmailAgent.Connectors.OpenAI.Http;
using Json.Schema;

namespace EmailAgent.Api.Models;

/// <summary>
/// M3T2 — builds the canonical tool-calling probe request (SQ-M3-3): the
/// <see cref="AgentRequest"/> handed to M3T1's <see cref="IAgentEngine"/>
/// interface, and the OpenAI wire body the audit row's
/// <c>request_excerpt</c> serializes.
/// </summary>
/// <remarks>
/// <para>
/// <b>No API key in the wire body.</b> The key travels only inside
/// <see cref="AgentContext.EngineConfigJson"/> (SQ-M3-10), from which the
/// engine reads it for the <c>Authorization: Bearer</c> header — never into a
/// serialized body. <see cref="BuildWireBody"/> therefore emits exactly the
/// canonical OpenAI fields (model, messages, tools, tool_choice, max_tokens,
/// stream) and nothing else, so the audit row can never leak the credential.
/// </para>
/// <para>
/// <b>Fixed probe prompt.</b> The bare string <c>"ping"</c> carries no
/// SQ-M3-9 <c>SYSTEM:/USER:</c> markers, so the engine's lenient fallback
/// treats it as one user message — the emitted body is exactly SQ-M3-3's
/// canonical shape. <see cref="SplitPrompt"/> mirrors that marker convention
/// (the engine's helper is private to M3T1, which this task does not modify).
/// </para>
/// <para>
/// <b><c>Message: null!</c>.</b> The probe is engine-only, not message-driven
/// (documented task Risk); M5's worker pool always populates a real
/// <see cref="EmailAgent.Contracts.MessageSources.RemoteMessage"/>.
/// </para>
/// </remarks>
public static class ProbeRequestBuilder
{
    /// <summary>The single tool the probe declares; the pass condition matches exactly this name.</summary>
    public const string ProbeToolName = "ping";

    /// <summary>The fixed probe prompt — SQ-M3-3's single user message; never user data.</summary>
    public const string ProbePrompt = "ping";

    /// <summary>SQ-M3-3's fixed tool description (also what the engine emits from the schema's own annotation).</summary>
    public const string ProbeToolDescription = "Probe tool; returns nothing.";

    /// <summary>SQ-M3-3's <c>max_tokens: 1</c> — bounds the cost of a probe call.</summary>
    public const int ProbeMaxTokens = 1;

    /// <summary>The SQ-M3-9 markers, mirrored from M3T1's <c>OpenAIRequestBuilder</c>.</summary>
    private const string SystemMarker = "SYSTEM:\n";

    private const string UserMarker = "\n\nUSER:\n";

    /// <summary>
    /// <c>ping()</c> takes no arguments: an object with an empty
    /// <c>properties</c> map and no additional properties. The engine maps this
    /// to the tool's <c>parameters</c>; <see cref="BuildWireBody"/> emits the
    /// full canonical triple independently of the engine's flattening.
    /// </summary>
    private static readonly JsonSchema PingSchema = new JsonSchemaBuilder()
        .Type(SchemaValueType.Object)
        .Description(ProbeToolDescription)
        .Properties()
        .AdditionalProperties(false)
        .Build();

    /// <summary>
    /// The canonical SQ-M3-3 <c>parameters</c> document, declared as the exact
    /// canonical text so the audit <c>request_excerpt</c> is byte-for-byte
    /// stable (no keyword-order or serializer-policy drift). Static and
    /// read-only, so its <see cref="JsonElement"/> never outlives its document.
    /// </summary>
    private static readonly JsonDocument PingParameters = JsonDocument.Parse(
        """{"type":"object","properties":{},"additionalProperties":false}""");

    /// <summary>
    /// Builds the probe request handed to the engine's interface method
    /// <c>CompleteAsync(AgentRequest, CancellationToken)</c> — the default
    /// non-streaming path, so the result is deterministic and bounded.
    /// </summary>
    /// <param name="baseUrl">The model's OpenAI-compatible endpoint (goes to <c>EngineConfigJson.base_url</c>).</param>
    /// <param name="apiKey">The plaintext key (engine-side only; never serialized into an audit row — see Risks).</param>
    /// <param name="modelName">The model identifier (goes to <c>EngineConfigJson.model_name</c>).</param>
    public static AgentRequest BuildProbeRequest(string baseUrl, string apiKey, string modelName)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(modelName);

        // A dictionary (not an anonymous object) keeps the member names
        // exactly as the engine's schema declares them, independent of any
        // serializer naming policy.
        var configJson = JsonSerializer.SerializeToElement(
            new Dictionary<string, string>
            {
                ["base_url"] = baseUrl,
                ["api_key"] = apiKey,
                ["model_name"] = modelName,
            });

        return new AgentRequest(
            Message: null!,
            Prompt: ProbePrompt,
            AllowedActionSchemas: new Dictionary<string, JsonSchema> { [ProbeToolName] = PingSchema },
            Context: new AgentContext(
                UserId: Guid.Empty,
                SourceId: Guid.Empty,
                Ct: CancellationToken.None)
            {
                EngineConfigJson = configJson,
            });
    }

    /// <summary>
    /// Builds the OpenAI wire body the audit row serializes as
    /// <c>request_excerpt</c>: the canonical SQ-M3-3 shape with the probe's
    /// model, one user message carrying the fixed prompt, the single
    /// <c>ping</c> tool, <c>tool_choice: "auto"</c>, <c>max_tokens: 1</c> and
    /// <c>stream: false</c>. The API key is NEVER part of this body.
    /// </summary>
    /// <param name="req">The request built by <see cref="BuildProbeRequest"/> (source of prompt, model and tool schema).</param>
    public static OpenAIWireBody BuildWireBody(AgentRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);

        var model = req.Context.EngineConfigJson is { } config
                    && config.ValueKind == JsonValueKind.Object
                    && config.TryGetProperty("model_name", out var modelName)
                    && modelName.ValueKind == JsonValueKind.String
            ? modelName.GetString() ?? string.Empty
            : string.Empty;

        // SQ-M3-9 splitting (mirrored): the bare "ping" prompt has no markers,
        // so the fallback yields exactly one user message — the canonical body.
        var (systemContent, userContent) = SplitPrompt(req.Prompt);
        IReadOnlyList<OpenAIWireMessage> messages = systemContent is null
            ? [new OpenAIWireMessage("user", userContent ?? string.Empty)]
            :
            [
                new OpenAIWireMessage("system", systemContent),
                new OpenAIWireMessage("user", userContent ?? string.Empty),
            ];

        var tools = new List<OpenAIWireTool>(req.AllowedActionSchemas.Count);
        foreach (var (typeId, _) in req.AllowedActionSchemas)
        {
            tools.Add(new OpenAIWireTool(typeId, ProbeToolDescription, PingParameters.RootElement));
        }

        return new OpenAIWireBody(
            Model: model,
            Messages: messages,
            Tools: tools,
            ToolChoice: "auto",
            MaxTokens: ProbeMaxTokens,
            Stream: false);
    }

    /// <summary>
    /// Splits a prompt on the SQ-M3-9 markers (semantically identical to M3T1's
    /// private helper, which this task must not modify): a prompt without both
    /// markers falls back to a single user message carrying the whole text.
    /// </summary>
    private static (string? System, string? User) SplitPrompt(string prompt)
    {
        if (prompt.StartsWith(SystemMarker, StringComparison.Ordinal))
        {
            var userIndex = prompt.IndexOf(UserMarker, StringComparison.Ordinal);
            if (userIndex >= 0)
            {
                return (
                    prompt.Substring(SystemMarker.Length, userIndex - SystemMarker.Length),
                    prompt.Substring(userIndex + UserMarker.Length));
            }
        }

        return (null, prompt);
    }
}

/// <summary>
/// The canonical OpenAI-compatible probe wire body (SQ-M3-3) — declaration
/// order is the canonical serialization order: <c>model</c>, <c>messages</c>,
/// <c>tools</c>, <c>tool_choice</c>, <c>max_tokens</c>, <c>stream</c>. Exposed
/// for test assertions; carries no credential.
/// </summary>
public sealed record OpenAIWireBody(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<OpenAIWireMessage> Messages,
    [property: JsonPropertyName("tools")] IReadOnlyList<OpenAIWireTool> Tools,
    [property: JsonPropertyName("tool_choice")] string ToolChoice,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    [property: JsonPropertyName("stream")] bool Stream);

/// <summary>One chat message — <c>{"role":"...","content":"..."}</c>.</summary>
public sealed record OpenAIWireMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

/// <summary>
/// One tool declaration, serialized as the nested OpenAI shape
/// <c>{"type":"function","function":{...}}</c>. Built from the flat
/// (name, description, parameters) triple the task's builder snippet uses.
/// </summary>
public sealed record OpenAIWireTool
{
    /// <summary>Creates a function tool declaration.</summary>
    /// <param name="name">The function name (the schema key / <c>typeId</c>, here always <c>ping</c>).</param>
    /// <param name="description">The fixed probe description (SQ-M3-3).</param>
    /// <param name="parameters">The canonical ping JSON-Schema parameters document.</param>
    public OpenAIWireTool(string name, string description, JsonElement parameters)
    {
        Function = new OpenAIWireFunction(name, description, parameters);
    }

    /// <summary>Always <c>function</c> — the only tool type the probe uses.</summary>
    [JsonPropertyName("type")]
    public string Type => "function";

    /// <summary>The nested function declaration.</summary>
    [JsonPropertyName("function")]
    public OpenAIWireFunction Function { get; }
}

/// <summary>The function declaration — name, description and JSON-Schema parameters.</summary>
public sealed record OpenAIWireFunction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parameters")] JsonElement Parameters);
