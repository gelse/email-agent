using System.Text.Json;
using EmailAgent.Contracts;
using EmailAgent.Contracts.MessageSources;
using Json.Schema;

namespace EmailAgent.Connectors.OpenAI;

/// <summary>
/// M2T3 stub of the OpenAI-compatible source connector (§5 DEFAULT plugin
/// set). It exists so the build graph and the DefaultTrustAnchor load path
/// are real; the implementation lands in M3. The loader instantiates this
/// type under <c>plugin_id=openai-compatible</c> at startup — construction
/// must therefore succeed, while every data-plane member throws
/// <see cref="NotImplementedException"/>.
/// </summary>
public sealed class OpenAIStubMessageSource : IMessageSource
{
    /// <summary>Stable type id — equals the manifest <c>plugin_id</c>.</summary>
    public string TypeId => "openai-compatible";

    /// <inheritdoc />
    public JsonSchema ConfigSchema =>
        throw new NotImplementedException("The OpenAI-compatible connector ships in M3 (M2T3 stub only).");

    /// <inheritdoc />
    public Task<ValidationResult> ValidateConfigAsync(JsonElement config, CancellationToken ct) =>
        throw new NotImplementedException("The OpenAI-compatible connector ships in M3 (M2T3 stub only).");

    /// <inheritdoc />
    public IAsyncEnumerable<RemoteMessage> ListNewMessagesAsync(
        JsonElement config, SourceCheckpoint checkpoint, CancellationToken ct) =>
        throw new NotImplementedException("The OpenAI-compatible connector ships in M3 (M2T3 stub only).");

    /// <inheritdoc />
    public Task<SourceCheckpoint> AdvanceCheckpointAsync(
        JsonElement config,
        SourceCheckpoint checkpoint,
        IReadOnlyList<RemoteMessage> processed,
        CancellationToken ct) =>
        throw new NotImplementedException("The OpenAI-compatible connector ships in M3 (M2T3 stub only).");
}
