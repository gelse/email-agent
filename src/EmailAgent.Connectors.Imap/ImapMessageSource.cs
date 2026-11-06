using System.Text.Json;
using EmailAgent.Contracts;
using EmailAgent.Contracts.MessageSources;
using EmailAgent.Contracts.Schema;
using Json.Schema;

namespace EmailAgent.Connectors.Imap;

/// <summary>
/// M2T3 skeleton of the DEFAULT IMAP source connector (§9 / §5). The loader
/// (M2T3) instantiates and registers this type under <c>plugin_id=imap</c>;
/// the real listing/validation/checkpoint implementation lands in M2T4, which
/// is why every data-plane member throws <see cref="NotImplementedException"/>.
/// </summary>
public sealed class ImapMessageSource : IMessageSource
{
    /// <summary>Stable type id — equals the manifest <c>plugin_id</c>.</summary>
    public string TypeId => "imap";

    /// <summary>M2T2's single source of truth (Q-F4 / SQ-1) — already final.</summary>
    public JsonSchema ConfigSchema => ImapConfigSchema.Build();

    /// <inheritdoc />
    public Task<ValidationResult> ValidateConfigAsync(JsonElement config, CancellationToken ct) =>
        throw new NotImplementedException("IMAP config validation lands in M2T4 (M2T3 ships the loader skeleton only).");

    /// <inheritdoc />
    public IAsyncEnumerable<RemoteMessage> ListNewMessagesAsync(
        JsonElement config, SourceCheckpoint checkpoint, CancellationToken ct) =>
        throw new NotImplementedException("IMAP message listing lands in M2T4.");

    /// <inheritdoc />
    public Task<SourceCheckpoint> AdvanceCheckpointAsync(
        JsonElement config,
        SourceCheckpoint checkpoint,
        IReadOnlyList<RemoteMessage> processed,
        CancellationToken ct) =>
        throw new NotImplementedException("IMAP checkpoint advancement lands in M2T4.");
}
