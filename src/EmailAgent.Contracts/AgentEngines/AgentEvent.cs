using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmailAgent.Contracts.AgentEngines;

/// <summary>
/// The sealed agent-event hierarchy streamed from
/// <see cref="IAgentEngine.CompleteAsync(AgentRequest, CancellationToken)"/>
/// (§9 "Agent event and context shapes"; M2T1 contract, verbatim kinds).
/// Success-path events only — <c>"text"</c>, <c>"tool_call"</c> and
/// <c>"done"</c>; errors are thrown as typed exceptions, never events
/// (Q-CE1). Wire shape: <c>{"kind":"text","text":"..."}</c>.
/// </summary>
[JsonConverter(typeof(AgentEventConverterFactory))]
public abstract record AgentEvent(string Kind);

/// <summary>
/// A fragment of streamed response text — <c>{"kind":"text","text":"..."}</c>.
/// </summary>
[JsonConverter(typeof(AgentEventConverterFactory))]
public sealed record AgentTextChunk(string Text) : AgentEvent("text");

/// <summary>
/// A model-requested tool call — <c>{"kind":"tool_call","name":"...","arguments":{...}}</c>.
/// <see cref="Arguments"/> must be a JSON <b>object</b> (enforced on read and
/// write) and, like every <see cref="JsonElement"/>, is tied to its
/// <see cref="JsonDocument"/>: clone it before disposing the source document —
/// the same lifetime contract <c>SourceCheckpoint.Extra</c> documents.
/// </summary>
[JsonConverter(typeof(AgentEventConverterFactory))]
public sealed record AgentToolCall(string Name, JsonElement Arguments) : AgentEvent("tool_call");

/// <summary>
/// Terminal success event with optional token usage —
/// <c>{"kind":"done","prompt_tokens":1,"completion_tokens":2}</c>. Both fields
/// are <see langword="null"/> when the provider reported no usage (M3's
/// additive <c>TotalTokens</c> / <c>ProviderUsage</c> extend this record later).
/// </summary>
[JsonConverter(typeof(AgentEventConverterFactory))]
public sealed record AgentDone(int? PromptTokens, int? CompletionTokens) : AgentEvent("done");

/// <summary>
/// Attaches the agent-event converter to every member of the
/// <see cref="AgentEvent"/> hierarchy. System.Text.Json resolves
/// <c>[JsonConverter]</c> without inheritance, so the attribute must sit on
/// the abstract base (declared-type serialization) <b>and</b> on each sealed
/// record (concrete static types, list elements, runtime-type serialization) —
/// otherwise concrete-typed calls fall back to the default record shape and
/// silently break the <c>{"kind":...}</c> wire contract (M2T1 review fix).
/// </summary>
internal sealed class AgentEventConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) =>
        typeof(AgentEvent).IsAssignableFrom(typeToConvert);

    /// <inheritdoc />
    public override JsonConverter CreateConverter(
        Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(AgentEventConverter<>).MakeGenericType(typeToConvert))!;
}

/// <summary>
/// Manual System.Text.Json converter for one static type of the
/// <see cref="AgentEvent"/> hierarchy: writes and reads the exact M2T1 wire
/// shape above, with the <c>kind</c> discriminator first and no <c>$type</c>
/// metadata (M2T1 step 11). The <c>kind</c> literal is derived from the
/// runtime type, never from the mutable <c>Kind</c> property; a parsed event
/// that is not assignable to the requested static type is rejected.
/// </summary>
internal sealed class AgentEventConverter<T> : JsonConverter<T>
    where T : AgentEvent
{
    /// <inheritdoc />
    public override T? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException($"agent event must be a JSON object, was {root.ValueKind}");
        if (!root.TryGetProperty("kind", out var kindEl) || kindEl.ValueKind != JsonValueKind.String)
            throw new JsonException("agent event is missing the string discriminator 'kind'");

        var kind = kindEl.GetString();
        AgentEvent evt = kind switch
        {
            "text" => new AgentTextChunk(RequiredString(root, "text")),
            "tool_call" => new AgentToolCall(
                RequiredString(root, "name"), RequiredArguments(root)),
            "done" => new AgentDone(
                NullableInt(root, "prompt_tokens"),
                NullableInt(root, "completion_tokens")),
            _ => throw new JsonException($"unknown agent event kind '{kind}'"),
        };

        if (evt is not T typed)
            throw new JsonException(
                $"agent event kind '{kind}' is not assignable to '{typeof(T).Name}'");
        return typed;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case AgentTextChunk text:
                writer.WriteString("kind", "text");
                writer.WriteString("text", text.Text);
                break;
            case AgentToolCall toolCall:
                writer.WriteString("kind", "tool_call");
                writer.WriteString("name", toolCall.Name);
                writer.WritePropertyName("arguments");
                if (toolCall.Arguments.ValueKind == JsonValueKind.Object)
                    toolCall.Arguments.WriteTo(writer);
                else if (toolCall.Arguments.ValueKind == JsonValueKind.Undefined)
                {
                    // Hand-built default(JsonElement): normalise to an empty
                    // object so what we write is always what Read accepts.
                    writer.WriteStartObject();
                    writer.WriteEndObject();
                }
                else
                    throw new JsonException(
                        "AgentToolCall.Arguments must be a JSON object (clone the JsonElement from its JsonDocument first)");
                break;
            case AgentDone done:
                writer.WriteString("kind", "done");
                WriteNullableInt(writer, "prompt_tokens", done.PromptTokens);
                WriteNullableInt(writer, "completion_tokens", done.CompletionTokens);
                break;
            default:
                throw new JsonException(
                    $"unsupported agent event type '{value.GetType().FullName}'");
        }
        writer.WriteEndObject();
    }

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new JsonException($"agent event field '{name}' must be a string");
        return value.GetString()!;
    }

    private static JsonElement RequiredArguments(JsonElement root)
    {
        if (!root.TryGetProperty("arguments", out var args) ||
            args.ValueKind != JsonValueKind.Object)
            throw new JsonException("agent event field 'arguments' must be a JSON object");
        return args.Clone();
    }

    private static int? NullableInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Number => value.TryGetInt32(out var number)
                ? number
                : throw new JsonException($"agent event field '{name}' exceeds the Int32 range"),
            _ => throw new JsonException($"agent event field '{name}' must be an integer or null"),
        };
    }

    private static void WriteNullableInt(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteNumber(name, value.Value);
    }
}
