using System.Text.Json;

namespace EmailAgent.Contracts.MessageSources;

/// <summary>
/// A source connector (e.g. the IMAP plugin, M2T4) that lists new messages
/// and advances a durable checkpoint (§9 "Core interfaces"; M2T1 step 12 —
/// interface only, implementations land in M2T4/M3/M8).
/// </summary>
public interface IMessageSource
{
    /// <summary>The connector's stable type id, e.g. <c>"imap"</c>.</summary>
    string TypeId { get; }

    /// <summary>JSON Schema describing the per-source configuration.</summary>
    JsonSchema ConfigSchema { get; }

    /// <summary>Validates a source configuration document against <see cref="ConfigSchema"/> and connector rules.</summary>
    Task<ValidationResult> ValidateConfigAsync(JsonElement config, CancellationToken ct);

    /// <summary>Lists messages newer than <paramref name="checkpoint"/>, in ascending order.</summary>
    IAsyncEnumerable<RemoteMessage> ListNewMessagesAsync(
        JsonElement config, SourceCheckpoint checkpoint, CancellationToken ct);

    /// <summary>Computes the checkpoint to persist after <paramref name="processed"/> messages were handled.</summary>
    Task<SourceCheckpoint> AdvanceCheckpointAsync(
        JsonElement config, SourceCheckpoint checkpoint,
        IReadOnlyList<RemoteMessage> processed, CancellationToken ct);
}
