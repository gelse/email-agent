using System.Text.Json;
using EmailAgent.Contracts.ActionHandlers;

namespace EmailAgent.Infrastructure.Actions;

/// <summary>M4T1: one engine <c>tool_call</c> handed to the dispatcher.</summary>
/// <remarks>
/// Mirrors <c>AgentToolCall(string Name, JsonElement Arguments)</c> as a
/// separate record so the dispatcher's <see cref="JsonElement"/> lifetime is
/// owned independently of the engine's event stream (clone before disposal).
/// </remarks>
public sealed record DispatchToolCall(string Name, JsonElement Arguments);

/// <summary>M4T1: one dispatch invocation (per processed message).</summary>
/// <param name="UserId">Owner of the agent config / action configs.</param>
/// <param name="AgentConfigId">The agent config whose whitelist and abort policy apply.</param>
/// <param name="ProcessedMessageId">Pre-existing <c>processed_messages</c> row (inserted at fetch, §10/Q-O9).</param>
/// <param name="ToolCalls">Engine <c>tool_call</c>s in declared order.</param>
/// <param name="SourceMessage">Originating-message snapshot (SQ-M4-4); required by imap handlers.</param>
/// <param name="UserDek">The caller's data-encryption key: opaque, never logged or persisted. Unchanged in type/signature by M4T1's mechanical swap — from M5T1 on the sole producer (the M5T5 pipeline call site) fills it from <c>ICryptoSessionAccessor.TryGetDek()</c> instead of a request header.</param>
/// <param name="DryRunOverride">Per-execution dry-run toggle (M5's approve-dry-run sets it; null = defer to config default).</param>
public sealed record DispatchRequest(
    Guid UserId,
    Guid AgentConfigId,
    Guid ProcessedMessageId,
    IReadOnlyList<DispatchToolCall> ToolCalls,
    SourceMessageSnapshot? SourceMessage,
    ReadOnlyMemory<byte> UserDek,
    bool? DryRunOverride = null);

/// <summary>M4T1: per-tool_call outcome.</summary>
public sealed record ActionDecision(
    int ToolCallIndex,
    string TypeId,
    ActionDecisionKind Kind,
    Guid? ActionConfigId,
    string? Error,
    string? DryRunDescriptionJson,
    Guid? PendingActionId);

/// <summary>M4T1: the decision vocabulary (§12 / Q-O17b / SQ-M4-1).</summary>
public enum ActionDecisionKind
{
    Executed,
    DryRun,
    PendingConfirmation,
    InvalidArgs,
    RejectedNotAllowed,
    HandlerNotFound,
    ConfigNotFound,
    Failed,
    SkippedAbort
}

/// <summary>M4T1: aggregate dispatch result.</summary>
public sealed record DispatchResult(IReadOnlyList<ActionDecision> Decisions);
