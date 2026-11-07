using System.Text.Json;

namespace EmailAgent.Connectors.OpenAI.Http;

/// <summary>
/// The single <see cref="JsonSerializerOptions"/> instance every OpenAI
/// wire-body write in this plugin uses (M3T1 Files: <c>OpenAIJsonOptions</c>).
/// <see cref="JsonSerializerDefaults.Web"/> matches the OpenAI-compatible
/// wire convention (camelCase names, case-insensitive reads) — the engine,
/// the M3T2 probe and the M3T6 test-completion service share this instance so
/// the request shape can never drift between them.
/// </summary>
public static class OpenAIJsonOptions
{
    /// <summary>Shared serializer options for OpenAI-compatible request/response bodies.</summary>
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web);
}
