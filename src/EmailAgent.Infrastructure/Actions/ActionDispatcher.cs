using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmailAgent.Contracts;
using EmailAgent.Contracts.ActionHandlers;
using EmailAgent.Core.Audit;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmailAgent.Infrastructure.Actions;

/// <summary>
/// M4T1: implements the §12 seven-step dispatcher flow (Q-AC1/AC2/AC3,
/// Q-L6/L7, SQ-M4-3/4/7/8). One EF transaction covers <c>pending_actions</c>
/// inserts, <c>user_notifications</c> inserts, and the
/// <c>processed_messages.actions_executed_json</c> update; audit rows are
/// emitted via <see cref="IAuditLogger"/> AFTER commit (M1T4 chain
/// semantics). A crash after a handler side effect but before commit can
/// lose the record of an executed action (documented window b, M4T1 Risks).
/// </summary>
public sealed class ActionDispatcher(
    EmailAgentDbContext db,
    IActionHandlerRegistry handlers,
    IActionConfigProvider configs,
    IAuditLogger audit,
    IOptions<ActionsOptions> options,
    ILogger<ActionDispatcher> logger)
    : IActionDispatcher
{
    private static readonly Meter Meter = new("EmailAgent.Actions", "1.0.0");
    private static readonly Counter<long> ResultCounter =
        Meter.CreateCounter<long>("email_agent_action_results");

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<DispatchResult> DispatchAsync(DispatchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ToolCalls);

        var agentConfig = await db.AgentConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == request.AgentConfigId && a.UserId == request.UserId, ct)
            .ConfigureAwait(false);
        if (agentConfig is null)
        {
            throw new AgentConfigNotFoundException(request.AgentConfigId, request.UserId);
        }

        var processed = await db.ProcessedMessages
            .FirstOrDefaultAsync(p => p.Id == request.ProcessedMessageId, ct)
            .ConfigureAwait(false);
        if (processed is null)
        {
            throw new ProcessedMessageNotFoundException(request.ProcessedMessageId);
        }

        var decisions = new List<ActionDecision>(request.ToolCalls.Count);
        var auditQueue = new List<AuditEvent>(request.ToolCalls.Count);
        var now = DateTimeOffset.UtcNow;
        var aborting = false;

        for (var i = 0; i < request.ToolCalls.Count; i++)
        {
            var call = request.ToolCalls[i];
            var args = call.Arguments.Clone();

            if (aborting)
            {
                var skipped = new ActionDecision(i, call.Name, ActionDecisionKind.SkippedAbort, null, null, null, null);
                decisions.Add(skipped);
                auditQueue.Add(BuildDispatchedAudit(request, i, call.Name, null, skipped));
                RecordMetric(call.Name, skipped.Kind);
                continue;
            }

            var (decision, notification, auditEvent) = await DispatchOneAsync(
                    request, agentConfig, i, call.Name, args, now, ct)
                .ConfigureAwait(false);

            decisions.Add(decision);
            if (notification is not null)
            {
                db.UserNotifications.Add(notification);
            }

            auditQueue.Add(auditEvent);
            RecordMetric(call.Name, decision.Kind);

            if (agentConfig.AbortOnActionFailure && IsFailure(decision.Kind))
            {
                aborting = true;
            }
        }

        MergeActionsExecuted(processed, decisions);

        await using (var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        // Audit after commit (M1T4): the HMAC chain append is outside the EF
        // transaction by design. Each row is appended independently; a failure
        // is logged and does not roll back the already-committed dispatch.
        foreach (var evt in auditQueue)
        {
            try
            {
                await audit.LogAsync(evt, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to append dispatch audit row action={Action} for processed_message={ProcessedMessageId}.",
                    evt.Action, request.ProcessedMessageId);
            }
        }

        return new DispatchResult(decisions);
    }

    private async Task<(ActionDecision Decision, UserNotification? Notification, AuditEvent Audit)> DispatchOneAsync(
        DispatchRequest request,
        AgentConfig agentConfig,
        int index,
        string name,
        JsonElement args,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // Step 2 (Q-AC1): strict whitelist.
        if (!agentConfig.AllowedActionTypeIds.Contains(name, StringComparer.Ordinal))
        {
            var d = Decision(index, name, ActionDecisionKind.RejectedNotAllowed, null,
                $"action type '{name}' is not in the agent's allowed_action_type_ids", null, null);
            return (d, null, BuildDispatchedAudit(request, index, name, null, d));
        }

        // Step 3a: handler.
        var handler = handlers.GetHandler(name);
        if (handler is null)
        {
            var d = Decision(index, name, ActionDecisionKind.HandlerNotFound, null,
                $"no handler registered for action type '{name}'", null, null);
            return (d, null, BuildDispatchedAudit(request, index, name, null, d));
        }

        // Step 3b: config.
        var envelope = await configs.ResolveAsync(request.UserId, name, request.UserDek, ct)
            .ConfigureAwait(false);
        if (envelope is null)
        {
            var d = Decision(index, name, ActionDecisionKind.ConfigNotFound, null,
                $"no action config found for type '{name}'", null, null);
            return (d, null, BuildDispatchedAudit(request, index, name, null, d));
        }

        // Step 4 (Q-AC2): args validation.
        ValidationResult validation;
        try
        {
            validation = await handler.ValidateArgsAsync(envelope.Config, args, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ValidateArgsAsync threw for action type {TypeId}.", name);
            validation = ValidationResult.Failure("args validation failed");
        }

        if (!validation.Ok)
        {
            var notification = BuildNotification(request, envelope, validation);
            var error = validation.Error ?? "invalid arguments";
            var d = Decision(index, name, ActionDecisionKind.InvalidArgs, envelope.Id, error, null, null);
            return (d, notification, BuildInvalidArgsAudit(request, index, name, envelope.Id, validation));
        }

        // Step 5 (SQ-M4-3): dry-run → confirmation → execute.
        var effectiveDryRun = request.DryRunOverride ?? envelope.DryRunDefault;
        var ctx = new ActionContext(request.UserId, request.ProcessedMessageId, ct)
        {
            DryRun = effectiveDryRun,
            SourceMessage = request.SourceMessage,
        };

        if (effectiveDryRun)
        {
            return await RunDryAsync(index, name, handler, envelope, args, ctx, request, ct).ConfigureAwait(false);
        }

        if (envelope.RequireConfirmation)
        {
            return PersistPending(index, name, envelope, args, request, now);
        }

        return await RunExecuteAsync(index, name, handler, envelope, args, ctx, request, ct).ConfigureAwait(false);
    }

    private async Task<(ActionDecision, UserNotification?, AuditEvent)> RunDryAsync(
        int index, string name, IActionHandler handler, ActionConfigEnvelope envelope,
        JsonElement args, ActionContext ctx, DispatchRequest request, CancellationToken ct)
    {
        try
        {
            var result = await handler.ExecuteAsync(envelope.Config, args, ctx, ct).ConfigureAwait(false);
            if (result.Ok)
            {
                var desc = ExtractDryRunDescription(result);
                var d = Decision(index, name, ActionDecisionKind.DryRun, envelope.Id, null, desc, null);
                return (d, null, BuildDispatchedAudit(request, index, name, envelope.Id, d));
            }

            var dFail = Decision(index, name, ActionDecisionKind.Failed, envelope.Id,
                result.Error ?? "dry-run failed", null, null);
            return (dFail, null, BuildDispatchedAudit(request, index, name, envelope.Id, dFail));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Dry-run ExecuteAsync threw for action type {TypeId}.", name);
            var d = Decision(index, name, ActionDecisionKind.Failed, envelope.Id, "dry-run failed", null, null);
            return (d, null, BuildDispatchedAudit(request, index, name, envelope.Id, d));
        }
    }

    private (ActionDecision, UserNotification?, AuditEvent) PersistPending(
        int index, string name, ActionConfigEnvelope envelope,
        JsonElement args, DispatchRequest request, DateTimeOffset now)
    {
        var expiry = options.Value.PendingConfirmationExpiry;
        var id = Guid.NewGuid();
        var pending = new PendingAction
        {
            Id = id,
            ProcessedMessageId = request.ProcessedMessageId,
            ActionConfigId = envelope.Id,
            ArgsJson = args.GetRawText(),
            Status = PendingActionStatus.Pending,
            RequestedAt = now,
            ExpiresAt = now + expiry,
            CreatedAt = now,
        };
        db.PendingActions.Add(pending);
        var d = Decision(index, name, ActionDecisionKind.PendingConfirmation, envelope.Id, null, null, id);
        return (d, null, BuildDispatchedAudit(request, index, name, envelope.Id, d));
    }

    private async Task<(ActionDecision, UserNotification?, AuditEvent)> RunExecuteAsync(
        int index, string name, IActionHandler handler, ActionConfigEnvelope envelope,
        JsonElement args, ActionContext ctx, DispatchRequest request, CancellationToken ct)
    {
        try
        {
            var result = await handler.ExecuteAsync(envelope.Config, args, ctx, ct).ConfigureAwait(false);
            if (result.Ok)
            {
                var d = Decision(index, name, ActionDecisionKind.Executed, envelope.Id, null, null, null);
                return (d, null, BuildDispatchedAudit(request, index, name, envelope.Id, d));
            }

            var dFail = Decision(index, name, ActionDecisionKind.Failed, envelope.Id,
                result.Error ?? "action failed", null, null);
            return (dFail, null, BuildDispatchedAudit(request, index, name, envelope.Id, dFail));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ExecuteAsync threw for action type {TypeId}.", name);
            var d = Decision(index, name, ActionDecisionKind.Failed, envelope.Id, "action failed", null, null);
            return (d, null, BuildDispatchedAudit(request, index, name, envelope.Id, d));
        }
    }

    private static ActionDecision Decision(
        int index, string typeId, ActionDecisionKind kind, Guid? configId,
        string? error, string? dryRunDesc, Guid? pendingId)
        => new(index, typeId, kind, configId, error, dryRunDesc, pendingId);

    private static bool IsFailure(ActionDecisionKind kind) => kind is
        ActionDecisionKind.RejectedNotAllowed or ActionDecisionKind.HandlerNotFound
        or ActionDecisionKind.ConfigNotFound or ActionDecisionKind.InvalidArgs
        or ActionDecisionKind.Failed;

    private static string ExtractDryRunDescription(ActionResult result)
    {
        var effects = result.SideEffects;
        if (effects is { Count: > 0 })
        {
            var wouldSend = effects.FirstOrDefault(e =>
                string.Equals(e.Kind, SideEffectKinds.WouldSendEmail, StringComparison.Ordinal));
            var detail = wouldSend?.Detail ?? effects[0].Detail;
            if (!string.IsNullOrEmpty(detail))
            {
                return detail;
            }
        }

        return "{\"dry_run\":true}";
    }

    private static void MergeActionsExecuted(ProcessedMessage processed, List<ActionDecision> decisions)
    {
        var byIndex = new Dictionary<int, JsonNode>();
        if (!string.IsNullOrWhiteSpace(processed.ActionsExecutedJson))
        {
            try
            {
                var existing = JsonNode.Parse(processed.ActionsExecutedJson);
                if (existing is JsonArray arr)
                {
                    foreach (var node in arr)
                    {
                        if (node?["tool_call_index"]?.GetValue<int>() is { } idx)
                        {
                            byIndex[idx] = node;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Corrupt existing value: overwrite with the new entries rather
                // than failing the dispatch (the record is best-effort state).
                byIndex.Clear();
            }
        }

        foreach (var d in decisions)
        {
            var entry = new JsonObject
            {
                ["tool_call_index"] = d.ToolCallIndex,
                ["type_id"] = d.TypeId,
                ["action_config_id"] = d.ActionConfigId,
                ["decision"] = DecisionWireName(d.Kind),
                ["error"] = d.Error,
                ["dry_run_description"] = d.DryRunDescriptionJson is null
                    ? null
                    : JsonNode.Parse(d.DryRunDescriptionJson),
                ["pending_action_id"] = d.PendingActionId,
            };
            byIndex[d.ToolCallIndex] = entry;
        }

        var merged = new JsonArray(byIndex.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToArray());
        processed.ActionsExecutedJson = merged.ToJsonString(JsonOpts);
    }

    private static string DecisionWireName(ActionDecisionKind kind) => kind switch
    {
        ActionDecisionKind.Executed => "executed",
        ActionDecisionKind.DryRun => "dry_run",
        ActionDecisionKind.PendingConfirmation => "pending_confirmation",
        ActionDecisionKind.InvalidArgs => "invalid_args",
        ActionDecisionKind.RejectedNotAllowed => "rejected_not_allowed",
        ActionDecisionKind.HandlerNotFound => "handler_not_found",
        ActionDecisionKind.ConfigNotFound => "config_not_found",
        ActionDecisionKind.Failed => "failed",
        ActionDecisionKind.SkippedAbort => "skipped_abort",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static AuditEvent BuildDispatchedAudit(
        DispatchRequest request, int index, string typeId, Guid? configId,
        ActionDecision decision)
    {
        var meta = new JsonObject
        {
            ["agent_config_id"] = request.AgentConfigId,
            ["processed_message_id"] = request.ProcessedMessageId,
            ["tool_call_index"] = index,
            ["type_id"] = typeId,
            ["action_config_id"] = configId,
            ["decision"] = DecisionWireName(decision.Kind),
            ["ok"] = decision.Kind is ActionDecisionKind.Executed or ActionDecisionKind.DryRun or ActionDecisionKind.PendingConfirmation,
            ["error"] = decision.Error,
            ["dry_run"] = decision.Kind == ActionDecisionKind.DryRun ? true : (bool?)null,
        };
        return new AuditEvent(
            request.UserId, "User", AuditActions.ActionDispatched,
            configId.HasValue ? "action_config" : null,
            configId?.ToString(),
            meta.ToJsonString(JsonOpts),
            null);
    }

    private static AuditEvent BuildInvalidArgsAudit(
        DispatchRequest request, int index, string typeId, Guid? configId, ValidationResult validation)
    {
        var issuesArr = new JsonArray();
        if (validation.Issues is not null)
        {
            foreach (var issue in validation.Issues.Take(10))
            {
                issuesArr.Add(new JsonObject { ["path"] = issue.Path, ["reason"] = issue.Reason });
            }
        }

        var meta = new JsonObject
        {
            ["agent_config_id"] = request.AgentConfigId,
            ["processed_message_id"] = request.ProcessedMessageId,
            ["tool_call_index"] = index,
            ["type_id"] = typeId,
            ["action_config_id"] = configId,
            ["decision"] = "invalid_args",
            ["issues"] = issuesArr,
        };
        return new AuditEvent(
            request.UserId, "User", AuditActions.ActionInvalidArgs,
            configId.HasValue ? "action_config" : null,
            configId?.ToString(),
            meta.ToJsonString(JsonOpts),
            null);
    }

    private static UserNotification BuildNotification(
        DispatchRequest request, ActionConfigEnvelope envelope, ValidationResult validation)
    {
        var issues = validation.Issues?.Take(10)
            .Select(i => $"{i.Path}: {i.Reason}")
            .ToArray() ?? Array.Empty<string>();
        var body = issues.Length == 0
            ? (validation.Error ?? "invalid arguments")
            : string.Join("; ", issues);
        if (body.Length > 500)
        {
            body = body[..500];
        }

        var display = string.IsNullOrWhiteSpace(envelope.DisplayName) ? envelope.TypeId : envelope.DisplayName;
        return new UserNotification
        {
            Id = Guid.NewGuid(),
            UserId = request.UserId,
            Kind = "action_invalid_args",
            Title = $"Action '{display}' rejected: invalid arguments",
            Body = body,
            RelatedType = "processed_message",
            RelatedId = request.ProcessedMessageId,
            CreatedAt = DateTimeOffset.UtcNow,
            ReadAt = null,
        };
    }

    private static void RecordMetric(string actionType, ActionDecisionKind kind)
    {
        try
        {
            ResultCounter.Add(1,
                new KeyValuePair<string, object?>("action_type", actionType),
                new KeyValuePair<string, object?>("result", DecisionWireName(kind)));
        }
        catch
        {
            // Metrics must never fail a dispatch.
        }
    }
}
