using System.Text.Json;
using EmailAgent.Contracts.AgentEngines;

namespace EmailAgent.Connectors.OpenAI.Http;

/// <summary>
/// Maps OpenAI-compatible responses onto the M2T1 <see cref="AgentEvent"/>
/// sealed hierarchy verbatim (M3T1 steps 7–8): text deltas →
/// <see cref="AgentTextChunk"/>, tool calls → <see cref="AgentToolCall"/>,
/// <c>finish_reason</c> / the non-streamed envelope → <see cref="AgentDone"/>.
/// No new event variant is ever produced — errors surface as
/// <see cref="EngineCallException"/>, never as events (§9 / Q-CE1 / SQ-M3-1).
/// </summary>
/// <remarks>
/// <b><see cref="JsonElement"/> lifetime:</b> every element handed out
/// (<see cref="AgentToolCall.Arguments"/>, <see cref="AgentDone.ProviderUsage"/>)
/// is <c>Clone()</c>d out of the parsed frame/response document, so callers may
/// consume it after the mapper disposed its own <see cref="JsonDocument"/> —
/// the same contract <c>AgentToolCall</c>'s XML doc states.
/// </remarks>
public static class OpenAIResponseMapper
{
    /// <summary>
    /// Maps one parsed SSE frame to zero or more events: a frame may carry a
    /// text delta, tool-call deltas, and the terminal <c>finish_reason</c> in
    /// any combination; role-only / empty deltas yield nothing, the
    /// <c>[DONE]</c> sentinel and an empty payload yield nothing, a choiceless
    /// frame with a <c>usage</c> block (the <c>stream_options.include_usage</c>
    /// tail chunk) yields that usage as the terminal <see cref="AgentDone"/>,
    /// and an <c>error</c> object, a non-object <c>choices[0]</c> or malformed
    /// JSON throws <see cref="EngineCallException"/> (status 200 — the transport
    /// call succeeded, the body is the failure).
    /// </summary>
    public static IReadOnlyList<AgentEvent> MapFrame(SseFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var data = frame.Data;
        if (string.IsNullOrWhiteSpace(data))
        {
            return Array.Empty<AgentEvent>();
        }

        if (data.Trim().Equals("[DONE]", StringComparison.Ordinal))
        {
            return Array.Empty<AgentEvent>();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(data);
        }
        catch (JsonException ex)
        {
            throw new EngineCallException(
                200, data, $"SSE data frame is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new EngineCallException(
                    200, data, $"SSE data frame must be a JSON object, was {root.ValueKind}");
            }

            ThrowWhenProviderError(root, data);

            List<AgentEvent> events = [];
            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                // Keep-alive / bookkeeping frame without a choice carries no
                // event — UNLESS it is the OpenAI `stream_options.include_usage`
                // tail chunk (`"choices": []` + `usage`): that usage is the only
                // copy left after the finish frame, so surface it as the
                // terminal AgentDone instead of dropping the token counts
                // (M3T1 review fix; the engine holds the terminal event until
                // end of stream so exactly one AgentDone reaches the caller).
                return root.TryGetProperty("usage", out var tailUsage)
                       && tailUsage.ValueKind == JsonValueKind.Object
                    ? new AgentEvent[] { MapDone(root) }
                    : events;
            }

            var choice = choices[0];
            if (choice.ValueKind != JsonValueKind.Object)
            {
                throw new EngineCallException(
                    200, data, $"choices[0] must be a JSON object, was {choice.ValueKind}");
            }

            if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
            {
                if (delta.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String)
                {
                    events.Add(new AgentTextChunk(content.GetString() ?? string.Empty));
                }

                if (delta.TryGetProperty("tool_calls", out var toolCalls)
                    && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in toolCalls.EnumerateArray())
                    {
                        var toolCall = MapToolCall(entry, data);
                        if (toolCall is not null)
                        {
                            events.Add(toolCall);
                        }
                    }
                }
            }

            if (choice.TryGetProperty("finish_reason", out var finishReason)
                && finishReason.ValueKind == JsonValueKind.String)
            {
                events.Add(MapDone(root));
            }

            return events;
        }
    }

    /// <summary>
    /// Maps a single non-streamed <c>chat.completion</c> response: one
    /// <see cref="AgentToolCall"/> per <c>message.tool_calls</c> entry first,
    /// then exactly one <see cref="AgentTextChunk"/> (empty when the provider
    /// reported no content), then <see cref="AgentDone"/> carrying the
    /// response's usage block. Missing or non-object <c>choices[0]</c>, a
    /// malformed envelope or an embedded <c>error</c> object throws
    /// <see cref="EngineCallException"/> (status 200).
    /// </summary>
    public static IEnumerable<AgentEvent> MapNonStreaming(JsonDocument response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var raw = response.RootElement.GetRawText();
        var root = response.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new EngineCallException(
                200, raw, $"response body must be a JSON object, was {root.ValueKind}");
        }

        ThrowWhenProviderError(root, raw);

        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            throw new EngineCallException(200, raw, "response body has no choices[0]");
        }

        var choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object)
        {
            throw new EngineCallException(
                200, raw, $"choices[0] must be a JSON object, was {choice.ValueKind}");
        }

        if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
        {
            throw new EngineCallException(200, raw, "response body has no choices[0].message object");
        }

        List<AgentEvent> events = [];

        if (message.TryGetProperty("tool_calls", out var toolCalls)
            && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in toolCalls.EnumerateArray())
            {
                var toolCall = MapToolCall(entry, raw);
                if (toolCall is not null)
                {
                    events.Add(toolCall);
                }
            }
        }

        var content = message.TryGetProperty("content", out var contentElement)
                      && contentElement.ValueKind == JsonValueKind.String
            ? contentElement.GetString()
            : null;
        events.Add(new AgentTextChunk(content ?? string.Empty));
        events.Add(MapDone(root));

        return events;
    }

    /// <summary>
    /// Reads the terminal usage of a frame or response into
    /// <see cref="AgentDone"/>; a body without a <c>usage</c> object yields
    /// null token counts (the M3T4 cost helper falls back to
    /// <c>cost_usd = NULL</c>).
    /// </summary>
    private static AgentDone MapDone(JsonElement root)
    {
        var hasUsage = root.TryGetProperty("usage", out var usage)
            && usage.ValueKind == JsonValueKind.Object;

        return new AgentDone(
            hasUsage ? ReadInt(usage, "prompt_tokens") : null,
            hasUsage ? ReadInt(usage, "completion_tokens") : null)
        {
            TotalTokens = hasUsage ? ReadInt(usage, "total_tokens") : null,
            ProviderUsage = hasUsage ? usage.Clone() : null,
        };
    }

    /// <summary>
    /// One <see cref="AgentToolCall"/> from a <c>tool_calls</c> array entry —
    /// <c>null</c> when the entry carries no usable function name (fragment
    /// accumulation for split name/arguments deltas is M5 territory; M3T1
    /// skips such entries instead of emitting a nameless tool call).
    /// OpenAI sends <c>arguments</c> as a JSON <b>string</b>; an empty or
    /// missing one maps to an empty object, an object payload is accepted as
    /// is (LiteLLM-style), and a malformed string throws
    /// <see cref="EngineCallException"/>.
    /// </summary>
    private static AgentToolCall? MapToolCall(JsonElement entry, string raw)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("function", out var function)
            || function.ValueKind != JsonValueKind.Object
            || !function.TryGetProperty("name", out var name)
            || name.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(name.GetString()))
        {
            return null;
        }

        JsonElement arguments;
        if (!function.TryGetProperty("arguments", out var argumentsElement)
            || argumentsElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            arguments = EmptyObject();
        }
        else if (argumentsElement.ValueKind == JsonValueKind.Object)
        {
            arguments = argumentsElement.Clone();
        }
        else if (argumentsElement.ValueKind == JsonValueKind.String)
        {
            var text = argumentsElement.GetString() ?? string.Empty;
            if (text.Trim().Length == 0)
            {
                arguments = EmptyObject();
            }
            else
            {
                JsonDocument argumentsDocument;
                try
                {
                    argumentsDocument = JsonDocument.Parse(text);
                }
                catch (JsonException ex)
                {
                    throw new EngineCallException(
                        200, raw, $"tool call '{name.GetString()}' arguments are not valid JSON: {ex.Message}");
                }

                using (argumentsDocument)
                {
                    if (argumentsDocument.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        throw new EngineCallException(
                            200,
                            raw,
                            $"tool call '{name.GetString()}' arguments must be a JSON object, was "
                            + argumentsDocument.RootElement.ValueKind);
                    }

                    arguments = argumentsDocument.RootElement.Clone();
                }
            }
        }
        else
        {
            throw new EngineCallException(
                200,
                raw,
                $"tool call '{name.GetString()}' arguments must be a JSON string, was {argumentsElement.ValueKind}");
        }

        return new AgentToolCall(name.GetString()!, arguments);
    }

    /// <summary>An embedded provider error inside a 2xx body is a call error, not an event (task step 7).</summary>
    private static void ThrowWhenProviderError(JsonElement root, string raw)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        var message = error.ValueKind == JsonValueKind.Object
                   && error.TryGetProperty("message", out var errorElement)
                   && errorElement.ValueKind == JsonValueKind.String
            ? errorElement.GetString()
            : error.GetRawText();

        throw new EngineCallException(200, raw, $"provider reported an error: {message}");
    }

    private static int? ReadInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    /// <summary>An independent empty-object element for tool calls without arguments.</summary>
    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
