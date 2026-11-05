using EmailAgent.Infrastructure.Enums;

namespace EmailAgent.Infrastructure.Entities;

/// <summary>§6 <c>model_catalog</c> row (admin-managed LLM catalogue).</summary>
public class ModelCatalogEntry
{
    public Guid Id { get; set; }

    public string DisplayName { get; set; } = null!;

    /// <summary>e.g. <c>openai-compatible</c>.</summary>
    public string Provider { get; set; } = null!;

    public string BaseUrl { get; set; } = null!;

    public string ModelName { get; set; } = null!;

    /// <summary>Pointer to a system-owned secret blob in <c>encrypted_blobs</c> (§7).</summary>
    public string? ApiKeySecretRef { get; set; }

    /// <summary>Admin-entered price used when the provider does not report cost.</summary>
    public decimal? PricePer1kInputUsd { get; set; }

    public decimal? PricePer1kOutputUsd { get; set; }

    public DateTimeOffset? ToolCallingProbedAt { get; set; }

    /// <summary>NULL means "never probed"; insert/update rejected unless <c>Pass</c> (Q-L4).</summary>
    public ToolCallingProbeResult? ToolCallingProbeResult { get; set; }

    /// <summary>§6 default <c>true</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Admin who created the entry (§6 FK to <c>users</c>).</summary>
    public Guid CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public User CreatedByUser { get; set; } = null!;

    public ICollection<AgentConfig> AgentConfigs { get; set; } = new List<AgentConfig>();
}