using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EmailAgent.Api.Actions;
using EmailAgent.Api.Pagination;
using FluentValidation;

namespace EmailAgent.Api.Endpoints;

public sealed class ActionConfigDto
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("type_id")] public string TypeId { get; set; } = string.Empty;
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("require_confirmation")] public bool RequireConfirmation { get; set; }
    [JsonPropertyName("dry_run_default")] public bool DryRunDefault { get; set; }
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Decrypted config with the <c>password</c> member stripped (never returned).</summary>
    [JsonPropertyName("config")] public JsonElement Config { get; set; }

    public static ActionConfigDto FromRow(ActionConfigRow row)
        => new()
        {
            Id = row.Entity.Id,
            TypeId = row.Entity.TypeId,
            DisplayName = row.Entity.DisplayName,
            RequireConfirmation = row.Entity.RequireConfirmation,
            DryRunDefault = row.Entity.DryRunDefault,
            CreatedAt = row.Entity.CreatedAt,
            Config = StripPassword(row.Config),
        };

    /// <summary>Removes any <c>password</c> property from an object config.</summary>
    public static JsonElement StripPassword(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object) return config.Clone();
        var node = JsonNode.Parse(config.GetRawText()) as JsonObject;
        if (node is null) return config.Clone();
        node.Remove("password");
        return JsonSerializer.SerializeToElement(node);
    }
}

public sealed record CreateActionRequest
{
    [JsonPropertyName("type_id")] public string? TypeId { get; init; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
    [JsonPropertyName("require_confirmation")] public bool? RequireConfirmation { get; init; }
    [JsonPropertyName("dry_run_default")] public bool? DryRunDefault { get; init; }
    [JsonPropertyName("config")] public JsonElement Config { get; init; }
}

public sealed record UpdateActionRequest
{
    [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
    [JsonPropertyName("require_confirmation")] public bool? RequireConfirmation { get; init; }
    [JsonPropertyName("dry_run_default")] public bool? DryRunDefault { get; init; }
    [JsonPropertyName("config")] public JsonElement Config { get; init; }
}

public sealed class CreateActionRequestValidator : AbstractValidator<CreateActionRequest>
{
    public CreateActionRequestValidator()
    {
        RuleFor(r => r.TypeId).NotEmpty().WithMessage("type_id is required.");
        RuleFor(r => r.DisplayName).Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("display_name must not be empty.");
        RuleFor(r => r.Config).Must(c => c.ValueKind == JsonValueKind.Object)
            .WithMessage(r => r.Config.ValueKind == JsonValueKind.Undefined ? "config is required." : "config must be a JSON object.");
    }
}

public sealed class UpdateActionRequestValidator : AbstractValidator<UpdateActionRequest>
{
    public UpdateActionRequestValidator()
    {
        RuleFor(r => r.DisplayName).Must(v => v is null || !string.IsNullOrWhiteSpace(v)).WithMessage("display_name must not be empty.");
        RuleFor(r => r.Config).Must(c => c.ValueKind is JsonValueKind.Undefined or JsonValueKind.Object)
            .WithMessage("config must be a JSON object.");
    }
}

[JsonSerializable(typeof(ActionConfigDto))]
[JsonSerializable(typeof(PagedResult<ActionConfigDto>))]
public partial class ActionsJsonContext : JsonSerializerContext { }
