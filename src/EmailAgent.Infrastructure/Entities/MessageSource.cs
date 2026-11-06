using EmailAgent.Infrastructure.Enums;

namespace EmailAgent.Infrastructure.Entities;

/// <summary>
/// §6 <c>message_sources</c> row. A user may have multiple sources (Q-C4).
/// <c>checkpoint_json</c> is the connector cursor (default <c>'{}'</c>).
/// </summary>
public class MessageSource
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>e.g. <c>imap</c> (M2 plugin type id).</summary>
    public string TypeId { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    /// <summary>DEK-encrypted connector config blob (§7).</summary>
    public Guid ConfigBlobId { get; set; }

    /// <summary>Poll interval (Q-O4 minimum 1 minute).</summary>
    public int IntervalMinutes { get; set; } = 5;

    public string CheckpointJson { get; set; } = "{}";

    public MessageSourceStatus Status { get; set; }

    public DateTimeOffset? LastRunAt { get; set; }

    public DateTimeOffset? NextRunAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public User User { get; set; } = null!;

    public EncryptedBlob ConfigBlob { get; set; } = null!;

    public ICollection<AgentConfig> AgentConfigs { get; set; } = new List<AgentConfig>();

    public ICollection<ScheduledJob> ScheduledJobs { get; set; } = new List<ScheduledJob>();

    public ICollection<ProcessedMessage> ProcessedMessages { get; set; } = new List<ProcessedMessage>();
}
