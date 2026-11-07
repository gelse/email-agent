using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentValidation;
using Json.Schema;

namespace EmailAgent.Api.Endpoints;

// ---------------------------------------------------------------------------
// M3T6: the POST /v1/admin/models/{id}/test-completion wire contract (task
// scope table). Member names are pinned with [JsonPropertyName] and every
// body serializes through the source-gen context below (task step 7), so the
// snake_case names are the published contract, not a naming-policy accident.
//
// The response carries NO api_key, NO base_url and NO provider header — the
// §13.12 surface is exactly {response_text, tool_calls, prompt_tokens,
// completion_tokens, total_tokens, cost_usd, duration_ms} (task scope table).
// ---------------------------------------------------------------------------

/// <summary>Body of <c>POST /v1/admin/models/{id}/test-completion</c> (task scope table).</summary>
public sealed record TestCompletionRequest
{
    /// <summary>Optional; a null or empty value means "no system prompt" (task scope table).</summary>
    [JsonPropertyName("system_prompt")]
    public string? SystemPrompt { get; init; }

    /// <summary>Required — an empty/whitespace value is a 400 <c>/errors/config-validation</c>.</summary>
    [JsonPropertyName("user_prompt")]
    public string? UserPrompt { get; init; }

    /// <summary>Optional tool declarations handed to the engine as allowed action schemas.</summary>
    [JsonPropertyName("tools")]
    public IReadOnlyList<ToolSpecDto>? Tools { get; init; }
}

/// <summary>
/// One tool declaration (task scope table): <c>name</c>, optional
/// <c>description</c> and the JSON-Schema <c>parameters</c> document.
/// </summary>
public sealed record ToolSpecDto
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Typed as <see cref="JsonNode"/> so a non-object payload (array,
    /// string, number) still BINDS and then fails validation with a field
    /// error — a converter-level rejection would bypass the §13.12
    /// <c>errors[]</c> shape the task requires (task error table).
    /// </summary>
    [JsonPropertyName("parameters")]
    public JsonNode? Parameters { get; init; }
}

/// <summary>One model-requested tool call in the 200 response (task scope table).</summary>
public sealed record ToolCallDto
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>The call's argument object, echoed verbatim from the engine event.</summary>
    [JsonPropertyName("arguments")]
    public JsonElement Arguments { get; init; }
}

/// <summary>
/// 200 body of the test-completion endpoint (task scope table).
/// <c>prompt_tokens</c>/<c>completion_tokens</c> are nullable on purpose
/// (M3T4 deviation D1): a provider that reports no usage must surface JSON
/// nulls — never a fabricated 0 — and <c>cost_usd</c> then stays null too.
/// </summary>
public sealed record TestCompletionResponse
{
    /// <summary>Concatenation of the <c>AgentTextChunk</c> events (empty when the model produced no text).</summary>
    [JsonPropertyName("response_text")]
    public string ResponseText { get; init; } = string.Empty;

    /// <summary>Null when the response carried no tool calls (task scope table).</summary>
    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<ToolCallDto>? ToolCalls { get; init; }

    [JsonPropertyName("prompt_tokens")]
    public int? PromptTokens { get; init; }

    [JsonPropertyName("completion_tokens")]
    public int? CompletionTokens { get; init; }

    /// <summary><c>TokenCounts.EffectiveTotalTokens</c> (0 when usage was absent).</summary>
    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; init; }

    /// <summary>M3T4's derived cost — null when neither provider nor prices produced one.</summary>
    [JsonPropertyName("cost_usd")]
    public decimal? CostUsd { get; init; }

    [JsonPropertyName("duration_ms")]
    public int DurationMs { get; init; }
}

/// <summary>
/// Task step 7: the source-gen context this namespace binds and writes with
/// (AOT-friendly; no reflection fallback).
/// </summary>
[JsonSerializable(typeof(TestCompletionRequest))]
[JsonSerializable(typeof(TestCompletionResponse))]
public partial class TestCompletionJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Task step 4: request-shape validation. Every failure below is a §13.12
/// 400 <c>/errors/config-validation</c> document (the task's error table) —
/// <b>not</b> <c>/errors/validation</c>, which is M3T3's class for the
/// catalog CRUD shapes. Property names are overridden to their lowercase
/// member segments so the handler's wire-name transform yields
/// <c>user_prompt</c>, <c>tools[0].name</c> and <c>tools[0].parameters</c>
/// exactly as the task's error table names them.
/// </summary>
public sealed class TestCompletionRequestValidator : AbstractValidator<TestCompletionRequest>
{
    public TestCompletionRequestValidator()
    {
        RuleFor(request => request.UserPrompt)
            .Must(userPrompt => !string.IsNullOrWhiteSpace(userPrompt))
            .WithMessage("user_prompt must not be empty.");

        // Review fix: a null element (`"tools":[null]`) belongs to the SAME
        // 400 input class, never an NRE-driven 500 — RuleForEach().NotNull()
        // is FluentValidation's documented null-element check. FV keeps the
        // collection indexer on element-level rules, so the wire name is
        // `tools[0]` — the same indexed family as `tools[0].name` /
        // `tools[0].parameters` (the override pins the lowercase base, and
        // WireFieldName passes already-lowercase heads through unchanged).
        RuleForEach(request => request.Tools)
            .NotNull()
            .OverridePropertyName("tools")
            .WithMessage("tools[] entries must not be null.");

        // `Where` must come directly after `RuleForEach` (FluentValidation's
        // documented contract): it drops null elements from the CHILD chain,
        // so the accessors below never dereference a null spec — the rule
        // above owns reporting them.
        RuleForEach(request => request.Tools).Where(tool => tool is not null).ChildRules(tool =>
        {
            tool.RuleFor(spec => spec.Name)
                .Must(name => !string.IsNullOrWhiteSpace(name))
                .OverridePropertyName("name")
                .WithMessage("tools[].name must not be empty.");

            tool.RuleFor(spec => spec.Parameters)
                .Must(parameters => parameters is JsonObject)
                .OverridePropertyName("parameters")
                .WithMessage("tools[].parameters must be a JSON object.");

            // An object is not necessarily a SCHEMA: JsonSchema.Net rejects
            // malformed keywords (`{"type":"bogus"}`) while parsing, which
            // would otherwise surface as an unhandled 500 deep inside the
            // engine call. Probing it here keeps every bad `parameters`
            // payload in the task's single 400 config-validation class.
            tool.RuleFor(spec => spec.Parameters)
                .Must(BeMaterializableSchema)
                .OverridePropertyName("parameters")
                .WithMessage("tools[].parameters must be a valid JSON Schema document.")
                .When(spec => spec.Parameters is JsonObject);
        });

        // The engine keys AllowedActionSchemas by tool name, so a duplicate
        // would throw inside the service (ToDictionary) — reject it here
        // instead (task Risks: JsonObject/tools[] handling).
        RuleFor(request => request.Tools)
            .Must(ToolNamesAreUnique)
            .WithMessage("tools[].name values must be unique.")
            .When(request => request.Tools is not null);
    }

    /// <summary>
    /// <see cref="JsonSchema.FromText"/> on the supplied document; any
    /// parse/conversion failure of admin-supplied schema text is a
    /// validation failure (400), never an unhandled server error.
    /// </summary>
    private static bool BeMaterializableSchema(JsonNode? parameters)
    {
        if (parameters is not JsonObject document)
        {
            return true; // the object rule above owns this case.
        }

        try
        {
            _ = JsonSchema.FromText(document.ToJsonString());
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ToolNamesAreUnique(IReadOnlyList<ToolSpecDto>? tools)
    {
        if (tools is null)
        {
            return true;
        }

        var names = tools
            .Where(spec => spec is not null && !string.IsNullOrWhiteSpace(spec.Name))
            .Select(spec => spec.Name!)
            .ToList();
        return names.Distinct(StringComparer.Ordinal).Count() == names.Count;
    }
}
