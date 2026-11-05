namespace EmailAgent.Infrastructure.Entities;

/// <summary>
/// §6 <c>agent_configs</c> row: which agent handles which source with which
/// model, plus the prefilter rule array (Q-L2) and allowed action types.
/// Exactly one of <c>prompt_text</c> / <c>prompt_blob_id</c> is set (§6).
/// </summary>
public class AgentConfig
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid SourceId { get; set; }

    public Guid ModelId { get; set; }

    /// <summary>Plaintext prompt when stored in the DB column (opt-in, provisional).</summary>
    public string? PromptText { get; set; }

    /// <summary>DEK-encrypted prompt (DEFAULT storage mode).</summary>
    public Guid? PromptBlobId { get; set; }

    /// <summary>Array of prefilter rules (Q-L2); default <c>'[]'</c>.</summary>
    public string PrefilterJson { get; set; } = "[]";

    /// <summary>Combiner for <see cref="PrefilterJson"/>: <c>AND</c> | <c>OR</c> (Q-L2c).</summary>
    public string PrefilterCombiner { get; set; } = "OR";

    /// <summary><c>text[]</c> of action type ids the agent may run.</summary>
    public string[] AllowedActionTypeIds { get; set; } = Array.Empty<string>();

    /// <summary>Q-AC3: default <c>false</c> (continue remaining actions).</summary>
    public bool AbortOnActionFailure { get; set; }

    public bool Enabled { get; set; } = true;

    public User User { get; set; } = null!;

    public MessageSource Source { get; set; } = null!;

    public ModelCatalogEntry Model { get; set; } = null!;

    public EncryptedBlob? PromptBlob { get; set; }
}