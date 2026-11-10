namespace EmailAgent.Connectors.OpenAI.Http;

/// <summary>
/// One server-sent-event frame out of the OpenAI-compatible SSE stream:
/// the accumulated <c>event</c> field (empty when the provider sent none —
/// OpenAI/LiteLLM never do) and the accumulated <c>data</c> payload
/// (M3T1 step 5).
/// </summary>
public sealed record SseFrame(string Event, string Data);
