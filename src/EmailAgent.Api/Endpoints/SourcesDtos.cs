using System.Text.Json;
using System.Text.Json.Serialization;
using EmailAgent.Api.Pagination;
using EmailAgent.Api.Sources;
using FluentValidation;

namespace EmailAgent.Api.Endpoints;

// ---------------------------------------------------------------------------
// M2T5: the /v1/user/sources wire contract (§13.13 shapes, task scope table).
// Every member name is pinned with [JsonPropertyName] because the responses
// serialize through the AOT-friendly source-gen context below (task step 8),
// whose default options carry no naming policy — the snake_case names are the
// published contract, not a policy accident.
//
// The `password` member does not exist on ANY of these types: the projection
// is built from SourceConfigDto, so no response path can leak a connector
// credential (task acceptance criterion).
// ---------------------------------------------------------------------------

/// <summary>
/// The connector configuration as returned to the client — the five §13.13
/// members of the IMAP config and no <c>password</c>. Non-IMAP plugins
/// (whose configs are schema-validated but not projected) yield null members.
/// </summary>
public sealed class SourceConfigDto
{
    [JsonPropertyName("host")]
    public string? Host { get; set; }

    [JsonPropertyName("port")]
    public int? Port { get; set; }

    [JsonPropertyName("use_ssl")]
    public bool? UseSsl { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("mailbox")]
    public string? Mailbox { get; set; }

    /// <summary>
    /// Projects a stored config document onto the wire shape. Only the five
    /// members above are read — <c>password</c> is deliberately never touched,
    /// so it cannot reach a response by accident.
    /// </summary>
    public static SourceConfigDto FromJson(JsonElement config)
    {
        var dto = new SourceConfigDto();
        if (config.ValueKind != JsonValueKind.Object)
        {
            return dto;
        }

        dto.Host = ReadString(config, "host");
        dto.Mailbox = ReadString(config, "mailbox");
        dto.Username = ReadString(config, "username");

        if (config.TryGetProperty("port", out var port))
        {
            dto.Port = port.ValueKind switch
            {
                JsonValueKind.Number when port.TryGetInt32(out var value) => value,
                JsonValueKind.String when int.TryParse(port.GetString(), out var value) => value,
                _ => null,
            };
        }

        if (config.TryGetProperty("use_ssl", out var useSsl))
        {
            dto.UseSsl = useSsl.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }

        return dto;
    }

    private static string? ReadString(JsonElement config, string name)
        => config.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>The connector checkpoint projection: <c>{"last_uid":…,"extra":{…}}</c>.</summary>
public sealed class SourceCheckpointDto
{
    [JsonPropertyName("last_uid")]
    public string? LastUid { get; set; }

    [JsonPropertyName("extra")]
    public IReadOnlyDictionary<string, JsonElement> Extra { get; set; }
        = new Dictionary<string, JsonElement>();
}

/// <summary>A message source as every endpoint returns it (never a password).</summary>
public sealed class SourceDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("type_id")]
    public string TypeId { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("interval_minutes")]
    public int IntervalMinutes { get; set; }

    [JsonPropertyName("checkpoint")]
    public SourceCheckpointDto Checkpoint { get; set; } = new();

    /// <summary>Null until the source has run; serialized as JSON null.</summary>
    [JsonPropertyName("last_run_at")]
    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>Null at create time (M5's scheduler owns it later); serialized as JSON null.</summary>
    [JsonPropertyName("next_run_at")]
    public DateTimeOffset? NextRunAt { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("config")]
    public SourceConfigDto Config { get; set; } = new();
}

/// <summary>Body of <c>POST /v1/user/sources</c>.</summary>
public sealed record CreateSourceRequest
{
    [JsonPropertyName("type_id")]
    public string? TypeId { get; init; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("interval_minutes")]
    public int? IntervalMinutes { get; init; }

    /// <summary>
    /// The connector config document. Non-nullable on purpose: an omitted
    /// member deserializes to <see cref="JsonValueKind.Undefined"/> (absent)
    /// and an explicit JSON <c>null</c> to <see cref="JsonValueKind.Null"/>,
    /// so the two cases stay distinguishable without a nullable-struct
    /// converter (M2T5 decision, task file).
    /// </summary>
    [JsonPropertyName("config")]
    public JsonElement Config { get; init; }
}

/// <summary>Body of <c>PATCH /v1/user/sources/{id}</c> — every member optional.</summary>
public sealed record UpdateSourceRequest
{
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("interval_minutes")]
    public int? IntervalMinutes { get; init; }

    /// <summary>Absent (<see cref="JsonValueKind.Undefined"/>) leaves the stored config untouched.</summary>
    [JsonPropertyName("config")]
    public JsonElement Config { get; init; }
}

/// <summary>Body of <c>POST /v1/user/sources/test-connection</c>.</summary>
public sealed record TestConnectionRequest
{
    [JsonPropertyName("type_id")]
    public string? TypeId { get; init; }

    [JsonPropertyName("config")]
    public JsonElement Config { get; init; }
}

/// <summary>Always-200 body of <c>/test-connection</c>: the failure is in the body, not the status (task Risks).</summary>
public sealed record TestConnectionResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>Body of <c>POST /v1/user/sources/{id}/fetch-now</c>.</summary>
public sealed record FetchNowResponse
{
    /// <summary>Messages the connector yielded this run.</summary>
    [JsonPropertyName("processed")]
    public int Processed { get; init; }

    /// <summary>Rows the dedupe-keyed insert actually accepted.</summary>
    [JsonPropertyName("new_processed_messages")]
    public int NewProcessedMessages { get; init; }

    [JsonPropertyName("checkpoint")]
    public SourceCheckpointDto Checkpoint { get; init; } = new();
}

/// <summary>
/// M2T5 task step 8: the source-gen context every <c>/v1/user/sources</c>
/// response serializes through (AOT-friendly; no reflection fallback).
/// </summary>
[JsonSerializable(typeof(SourceDto))]
[JsonSerializable(typeof(PagedResult<SourceDto>))]
[JsonSerializable(typeof(TestConnectionResponse))]
[JsonSerializable(typeof(FetchNowResponse))]
public partial class SourcesJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Request-shape validation (§14): these failures are 400
/// <c>/errors/validation</c> documents. Everything the plugin's
/// <c>ConfigSchema</c> rejects is a separate 400 <c>/errors/config-validation</c>
/// raised by <see cref="SourceService"/> — schema failures never reach here.
/// </summary>
public sealed class CreateSourceRequestValidator : AbstractValidator<CreateSourceRequest>
{
    public CreateSourceRequestValidator()
    {
        RuleFor(request => request.TypeId)
            .NotEmpty()
            .WithMessage("type_id is required.");

        RuleFor(request => request.DisplayName)
            .Must(value => !string.IsNullOrWhiteSpace(value))
            .WithMessage("display_name must not be empty.");

        RuleFor(request => request.IntervalMinutes)
            .InclusiveBetween(
                SourceService.MinIntervalMinutes, SourceService.MaxIntervalMinutes)
            .WithMessage(
                $"interval_minutes must be between {SourceService.MinIntervalMinutes} "
                + $"and {SourceService.MaxIntervalMinutes}.")
            .When(request => request.IntervalMinutes.HasValue);

        RuleFor(request => request.Config)
            .Must(config => config.ValueKind == JsonValueKind.Object)
            .WithMessage(request => request.Config.ValueKind == JsonValueKind.Undefined
                ? "config is required."
                : "config must be a JSON object.");
    }
}

/// <summary>PATCH validation: an absent member means "leave unchanged" (task Risks).</summary>
public sealed class UpdateSourceRequestValidator : AbstractValidator<UpdateSourceRequest>
{
    public UpdateSourceRequestValidator()
    {
        RuleFor(request => request.DisplayName)
            .Must(value => value is null || !string.IsNullOrWhiteSpace(value))
            .WithMessage("display_name must not be empty.");

        RuleFor(request => request.IntervalMinutes)
            .InclusiveBetween(
                SourceService.MinIntervalMinutes, SourceService.MaxIntervalMinutes)
            .WithMessage(
                $"interval_minutes must be between {SourceService.MinIntervalMinutes} "
                + $"and {SourceService.MaxIntervalMinutes}.")
            .When(request => request.IntervalMinutes.HasValue);

        RuleFor(request => request.Config)
            .Must(config => config.ValueKind is JsonValueKind.Undefined or JsonValueKind.Object)
            .WithMessage("config must be a JSON object.");
    }
}

/// <summary><c>/test-connection</c> request validation (schema failures stay in the service).</summary>
public sealed class TestConnectionRequestValidator : AbstractValidator<TestConnectionRequest>
{
    public TestConnectionRequestValidator()
    {
        RuleFor(request => request.TypeId)
            .NotEmpty()
            .WithMessage("type_id is required.");

        RuleFor(request => request.Config)
            .Must(config => config.ValueKind == JsonValueKind.Object)
            .WithMessage(request => request.Config.ValueKind == JsonValueKind.Undefined
                ? "config is required."
                : "config must be a JSON object.");
    }
}
