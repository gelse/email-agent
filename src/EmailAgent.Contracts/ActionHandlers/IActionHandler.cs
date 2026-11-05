using System.Text.Json;

namespace EmailAgent.Contracts.ActionHandlers;

/// <summary>
/// An action handler (e.g. <c>imap-reply</c>) that validates configuration and
/// arguments and executes side effects on behalf of the agent engine
/// (§9 "Core interfaces"; M2T1 step 9 — interface only, implementations land
/// in M4/M8).
/// </summary>
public interface IActionHandler
{
    /// <summary>The handler's stable type id, e.g. <c>"imap-reply"</c>.</summary>
    string TypeId { get; }

    /// <summary>JSON Schema describing the per-action configuration (e.g. SMTP values — Q-C4).</summary>
    JsonSchema ConfigSchema { get; }

    /// <summary>JSON Schema describing the arguments the model may pass.</summary>
    JsonSchema ArgsSchema { get; }

    /// <summary>Validates an action configuration document against <see cref="ConfigSchema"/> and handler rules.</summary>
    Task<ValidationResult> ValidateConfigAsync(JsonElement config, CancellationToken ct);

    /// <summary>Validates a model-supplied argument object against <see cref="ArgsSchema"/>.</summary>
    Task<ValidationResult> ValidateArgsAsync(JsonElement config, JsonElement args, CancellationToken ct);

    /// <summary>Executes the action, recording any <see cref="SideEffect"/>s on the result.</summary>
    Task<ActionResult> ExecuteAsync(
        JsonElement config, JsonElement args, ActionContext ctx, CancellationToken ct);
}
