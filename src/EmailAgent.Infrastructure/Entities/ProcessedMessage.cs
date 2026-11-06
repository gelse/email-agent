using EmailAgent.Infrastructure.Enums;

namespace EmailAgent.Infrastructure.Entities;

/// <summary>
/// §6 <c>processed_messages</c> row. Dedupe key: UNIQUE(<c>source_id</c>,
/// <c>remote_uid</c>). <c>source_id</c> is a reference to the source the
/// message was fetched from — note that §6/Q-O15 keep these rows when the
/// source is deleted ("orphan <c>processed_messages</c> rows are retained"),
/// so the FK is mapped as <c>Restrict</c> rather than <c>Cascade</c>.
/// </summary>
public class ProcessedMessage
{
    public Guid Id { get; set; }

    public Guid SourceId { get; set; }

    /// <summary>Remote message UID — dedupe key together with <c>source_id</c>.</summary>
    public string RemoteUid { get; set; } = null!;

    public DateTimeOffset? ReceivedRemoteAt { get; set; }

    /// <summary>Furthest pipeline stage reached (Q-O9); default <c>fetched</c>.</summary>
    public ProcessedMessageStage Stage { get; set; } = ProcessedMessageStage.fetched;

    /// <summary>Prefilter-skipped messages record <c>stage=prefiltered</c> here <c>false</c> (Q-O9b).</summary>
    public bool PrefilterMatched { get; set; }

    /// <summary>Engine output, structured (jsonb); NULL until decided.</summary>
    public string? ModelDecisionJson { get; set; }

    /// <summary>Per-action result array (jsonb); NULL until acted.</summary>
    public string? ActionsExecutedJson { get; set; }

    /// <summary>LLM usage — one engine call per message decision (§6 provisional).</summary>
    public int? PromptTokens { get; set; }

    public int? CompletionTokens { get; set; }

    public int? TotalTokens { get; set; }

    /// <summary>Reported by the provider or derived from <c>model_catalog</c> prices.</summary>
    public decimal? CostUsd { get; set; }

    /// <summary>DEK-encrypted full body when full-body retention is enabled (Q-LP1).</summary>
    public Guid? BodyBlobId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public MessageSource Source { get; set; } = null!;

    public EncryptedBlob? BodyBlob { get; set; }

    public ICollection<PendingAction> PendingActions { get; set; } = new List<PendingAction>();
}
