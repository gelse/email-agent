namespace EmailAgent.Connectors.OpenAI;

/// <summary>
/// Typed failure of one OpenAI-compatible engine call (M3T1 step 9). Errors are
/// never events (§9 / Q-CE1): the engine throws this instead, and the caller
/// (M3T2 probe, M3T6 test-completion) maps it to its own response shape.
/// </summary>
/// <remarks>
/// Two shapes share the type: a transport-level failure (HTTP non-2xx, where
/// <see cref="StatusCode"/> is the provider's status and
/// <see cref="ResponseBody"/> the raw error body) and a response the engine
/// could not map (malformed JSON / embedded error object, where
/// <see cref="StatusCode"/> is 200 — the call itself succeeded) with
/// <see cref="Detail"/> explaining the parse failure.
/// </remarks>
public sealed class EngineCallException : Exception
{
    /// <summary>Creates the exception for a failed engine call.</summary>
    /// <param name="statusCode">HTTP status of the response (200 when the body itself was the problem).</param>
    /// <param name="responseBody">Raw response body as received — never sanitized, never a secret of ours.</param>
    /// <param name="detail">Parse/error detail when the call failed inside a 2xx body; <c>null</c> for plain HTTP errors.</param>
    public EngineCallException(int statusCode, string responseBody, string? detail = null)
        : base(BuildMessage(statusCode, detail))
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
        Detail = detail;
    }

    /// <summary>HTTP status code of the response (or 200 when a 2xx body failed to map).</summary>
    public int StatusCode { get; }

    /// <summary>Raw response body as received.</summary>
    public string ResponseBody { get; }

    /// <summary>Parse/error detail for a 2xx body that could not be mapped; otherwise <c>null</c>.</summary>
    public string? Detail { get; }

    private static string BuildMessage(int statusCode, string? detail) =>
        detail is null
            ? $"OpenAI-compatible engine call failed with HTTP status {statusCode}."
            : $"OpenAI-compatible engine call failed: {detail} (HTTP status {statusCode}).";
}
