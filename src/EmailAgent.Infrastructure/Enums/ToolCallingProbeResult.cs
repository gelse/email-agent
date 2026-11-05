namespace EmailAgent.Infrastructure.Enums;

/// <summary>
/// §6 <c>model_catalog.tool_calling_probe_result</c>: enum(<c>Pass</c>,
/// <c>Fail</c>), NULL means "never probed". Stored as text with a mirroring
/// CHECK constraint per M1T2 task step 9 (avoids Postgres enum migration
/// friction while keeping the §6 value set enforced).
/// </summary>
public enum ToolCallingProbeResult
{
    Pass,
    Fail,
}