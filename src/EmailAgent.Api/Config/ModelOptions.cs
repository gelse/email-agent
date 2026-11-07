namespace EmailAgent.Api.Config;

/// <summary>
/// M3T2 — binds the configuration section <c>Model</c> (env
/// <c>MODEL__ProbeReprobeWindow</c>, §16 / SQ-M3-3): the tool-calling probe's
/// re-probe window. Deliberately its own section — never bound from
/// <c>Agent</c>/<c>AgentOptions</c> (task step 2), so an engine-call tuning knob
/// can never move the catalog gate by accident.
/// </summary>
public sealed class ModelOptions
{
    /// <summary>Configuration section name (env <c>MODEL__*</c> vars land here).</summary>
    public const string SectionName = "Model";

    /// <summary>
    /// How long a cached <c>tool_calling_probe_result = Pass</c> for the same
    /// <c>(base_url, model_name)</c> pair keeps serving without an HTTP call.
    /// Default 1 hour (<c>01:00:00</c>) per SQ-M3-3.
    /// </summary>
    public TimeSpan ProbeReprobeWindow { get; set; } = TimeSpan.FromHours(1);
}
