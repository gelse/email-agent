using System.Text.Json.Serialization;
using EmailAgent.Api.Pagination;
using FluentValidation;

namespace EmailAgent.Api.Endpoints;

// ---------------------------------------------------------------------------
// M3T3: the /v1/admin/models wire contract (§13.13 shapes, task scope table).
// Member names are pinned with [JsonPropertyName] because every response
// serializes through the AOT-friendly source-gen context below (task step 7)
// — the snake_case names are the published contract, not a naming-policy
// accident.
//
// ModelDto carries NO api_key, NO api_key_secret_ref and NO encrypted_blob_id
// (task "ModelDto projection"): the projection is a fixed member list, so no
// response path can leak a credential (acceptance criterion), and the
// encryption tests assert the deserialized property count matches exactly.
// ---------------------------------------------------------------------------

/// <summary>A model catalog entry as every endpoint returns it (never a key).</summary>
public sealed class ModelDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>e.g., <c>openai-compatible</c>.</summary>
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("base_url")]
    public string BaseUrl { get; set; } = string.Empty;

    [JsonPropertyName("model_name")]
    public string ModelName { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>Null until the entry has been probed; serialized as JSON null.</summary>
    [JsonPropertyName("tool_calling_probed_at")]
    public DateTimeOffset? ToolCallingProbedAt { get; set; }

    /// <summary>
    /// <c>Pass</c> / <c>Fails</c> as the §6 text mapping writes them
    /// (<c>tool_calling_probe_result IN ('Pass','Fail')</c>); null means "never
    /// probed" and serializes as JSON null.
    /// </summary>
    [JsonPropertyName("tool_calling_probe_result")]
    public string? ToolCallingProbeResult { get; set; }

    [JsonPropertyName("created_by")]
    public Guid CreatedBy { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("price_per_1k_input_usd")]
    public decimal? PricePer1kInputUsd { get; set; }

    [JsonPropertyName("price_per_1k_output_usd")]
    public decimal? PricePer1kOutputUsd { get; set; }
}

/// <summary>Body of <c>POST /v1/admin/models</c> (task scope table).</summary>
public sealed record CreateModelRequest
{
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    /// <summary>
    /// The three engine-config members are deliberately NOT required here:
    /// their shape is owned by M3T1's <c>OpenAICompatibleConfigSchema</c>, so a
    /// missing <c>base_url</c>/<c>api_key</c>/<c>model_name</c> surfaces as a
    /// 400 <c>/errors/config-validation</c> document (task RFC 7807 list),
    /// not a <c>/errors/validation</c> one.
    /// </summary>
    [JsonPropertyName("base_url")]
    public string? BaseUrl { get; init; }

    [JsonPropertyName("model_name")]
    public string? ModelName { get; init; }

    /// <summary>The credential — stored only as a system-wrapped blob (Q-E7).</summary>
    [JsonPropertyName("api_key")]
    public string? ApiKey { get; init; }

    [JsonPropertyName("price_per_1k_input_usd")]
    public decimal? PricePer1kInputUsd { get; init; }

    [JsonPropertyName("price_per_1k_output_usd")]
    public decimal? PricePer1kOutputUsd { get; init; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }
}

/// <summary>
/// Body of <c>PATCH /v1/admin/models/{id}</c> — every member optional, an
/// absent member means "leave unchanged". A body with no member at all is a
/// 400 (task Risks: a PATCH with an empty body has nothing to update).
/// </summary>
public sealed record UpdateModelRequest
{
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("base_url")]
    public string? BaseUrl { get; init; }

    [JsonPropertyName("model_name")]
    public string? ModelName { get; init; }

    /// <summary>Supplying a key re-wraps it into a NEW blob and deletes the old one (same transaction).</summary>
    [JsonPropertyName("api_key")]
    public string? ApiKey { get; init; }

    [JsonPropertyName("price_per_1k_input_usd")]
    public decimal? PricePer1kInputUsd { get; init; }

    [JsonPropertyName("price_per_1k_output_usd")]
    public decimal? PricePer1kOutputUsd { get; init; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }
}

/// <summary>
/// M3T3 task step 7: the source-gen context every <c>/v1/admin/models</c>
/// response serializes through (AOT-friendly; no reflection fallback). The
/// request bodies are read through it too, so the whole namespace binds and
/// writes with one pinned option set.
/// </summary>
[JsonSerializable(typeof(ModelDto))]
[JsonSerializable(typeof(PagedResult<ModelDto>))]
[JsonSerializable(typeof(CreateModelRequest))]
[JsonSerializable(typeof(UpdateModelRequest))]
public partial class AdminModelsJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Request-shape validation (§14): these failures are 400
/// <c>/errors/validation</c> documents. Everything M3T1's engine schema
/// rejects (bad <c>base_url</c>, empty <c>api_key</c>, unknown member) is a
/// separate 400 <c>/errors/config-validation</c> raised by
/// <see cref="EmailAgent.Api.Models.ModelService"/> — schema failures never
/// reach here.
/// </summary>
public sealed class CreateModelRequestValidator : AbstractValidator<CreateModelRequest>
{
    public CreateModelRequestValidator()
    {
        RuleFor(request => request.DisplayName)
            .Must(value => !string.IsNullOrWhiteSpace(value))
            .WithMessage("display_name must not be empty.");

        RuleFor(request => request.Provider)
            .Must(value => !string.IsNullOrWhiteSpace(value))
            .WithMessage("provider must not be empty.");

        RuleFor(request => request.ApiKey)
            .Must(value => value is null || !string.IsNullOrWhiteSpace(value))
            .WithMessage("api_key must not be empty.");

        RuleFor(request => request.PricePer1kInputUsd)
            .Must(value => value is null || value >= 0)
            .WithMessage("price_per_1k_input_usd must not be negative.")
            .When(request => request.PricePer1kInputUsd.HasValue);

        RuleFor(request => request.PricePer1kOutputUsd)
            .Must(value => value is null || value >= 0)
            .WithMessage("price_per_1k_output_usd must not be negative.")
            .When(request => request.PricePer1kOutputUsd.HasValue);
    }
}

/// <summary>PATCH validation: an absent member means "leave unchanged" (task Risks).</summary>
public sealed class UpdateModelRequestValidator : AbstractValidator<UpdateModelRequest>
{
    public UpdateModelRequestValidator()
    {
        // Task Risks: a PATCH with an empty body is a 400 — there is nothing
        // to update and, worse, nothing for the mandatory probe to gate.
        RuleFor(request => request)
            .Must(HasAtLeastOneMember)
            .OverridePropertyName("body")
            .WithMessage("at least one field is required.");

        RuleFor(request => request.DisplayName)
            .Must(value => value is null || !string.IsNullOrWhiteSpace(value))
            .WithMessage("display_name must not be empty.");

        RuleFor(request => request.Provider)
            .Must(value => value is null || !string.IsNullOrWhiteSpace(value))
            .WithMessage("provider must not be empty.");

        RuleFor(request => request.ApiKey)
            .Must(value => value is null || !string.IsNullOrWhiteSpace(value))
            .WithMessage("api_key must not be empty.");

        RuleFor(request => request.PricePer1kInputUsd)
            .Must(value => value is null || value >= 0)
            .WithMessage("price_per_1k_input_usd must not be negative.")
            .When(request => request.PricePer1kInputUsd.HasValue);

        RuleFor(request => request.PricePer1kOutputUsd)
            .Must(value => value is null || value >= 0)
            .WithMessage("price_per_1k_output_usd must not be negative.")
            .When(request => request.PricePer1kOutputUsd.HasValue);
    }

    private static bool HasAtLeastOneMember(UpdateModelRequest request)
        => request.DisplayName is not null
        || request.Provider is not null
        || request.BaseUrl is not null
        || request.ModelName is not null
        || request.ApiKey is not null
        || request.PricePer1kInputUsd.HasValue
        || request.PricePer1kOutputUsd.HasValue
        || request.Enabled.HasValue;
}
