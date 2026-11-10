using System.Net;
using System.Text;
using System.Text.Json;
using EmailAgent.Api.Endpoints;
using EmailAgent.Api.Pagination;
using EmailAgent.Contracts;
using EmailAgent.Contracts.MessageSources;
using EmailAgent.Contracts.Schema;
using EmailAgent.Core.Audit;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using EmailAgent.Infrastructure.Plugins;
using Microsoft.EntityFrameworkCore;

namespace EmailAgent.Api.Sources;

/// <summary>
/// The authenticated caller as the audit layer needs it: identity, role and
/// client address. Built by the endpoint layer so this service never depends
/// on <c>HttpContext</c>.
/// </summary>
public sealed record SourceActor(Guid UserId, string Role, IPAddress? Ip);

/// <summary>Body of <c>POST /v1/user/sources/test-connection</c> (always HTTP 200).</summary>
public sealed record TestConnectionOutcome(bool Ok, string? Error);

/// <summary>Body of <c>POST /v1/user/sources/{id}/fetch-now</c>.</summary>
public sealed record FetchOutcome(
    int Processed, int NewProcessedMessages, SourceCheckpoint Checkpoint);

/// <summary>
/// M2T5: the seven <c>/v1/user/sources</c> operations (scope table §In).
/// Ownership, plugin resolution, schema validation, DEK encryption, dedupe-keyed
/// persistence and the five §15 audit rows all live here; the endpoint layer
/// only translates HTTP (DEK header, RFC 7807 documents) and this service only
/// raises the typed failures in <see cref="SourceErrors"/>.
/// </summary>
/// <remarks>
/// <b>Ownership.</b> Every lookup filters <c>UserId == caller</c>, so a foreign
/// id is indistinguishable from an unknown one — <see cref="SourceNotFoundException"/>
/// is the only outcome (404, never 403; task Risks).
/// <b>Transactions.</b> Create, update, delete and fetch-now each run inside one
/// transaction that also carries the audit row, so SQ-7's rule holds: the
/// source row, its config blob and its audit evidence commit together or not
/// at all.
/// </remarks>
public sealed class SourceService(
    EmailAgentDbContext db,
    IPluginRegistry registry,
    EncryptedSourceConfigStore store,
    IAuditLogger audit)
{
    /// <summary>§6 <c>message_sources.interval_minutes</c> default (task scope table).</summary>
    public const int DefaultIntervalMinutes = 5;

    /// <summary>Q-O4: the poll interval may not be shorter than a minute.</summary>
    public const int MinIntervalMinutes = 1;

    /// <summary>Upper bound of the M2 interval knob — one day (M2T5 decision, task file).</summary>
    public const int MaxIntervalMinutes = 1440;

    /// <summary>
    /// <c>GET /v1/user/sources</c> — one keyset page of the caller's sources
    /// (§13.13), each with its decrypted configuration minus the password.
    /// </summary>
    public async Task<PagedResult<SourceDto>> ListAsync(
        SourceActor actor,
        byte[] dek,
        string? typeId,
        MessageSourceStatus? status,
        string? cursor,
        int limit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(dek);

        // Read-only projection: no need to track up to 100 rows per page.
        IQueryable<MessageSource> query = db.MessageSources
            .AsNoTracking()
            .Where(source => source.UserId == actor.UserId);

        if (!string.IsNullOrEmpty(typeId))
        {
            query = query.Where(source => source.TypeId == typeId);
        }

        if (status is { } statusValue)
        {
            query = query.Where(source => source.Status == statusValue);
        }

        var rows = await CursorPager.ApplyCursor(query, cursor, limit).ToListAsync(ct);

        var hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var items = new List<SourceDto>(rows.Count);
        foreach (var row in rows)
        {
            var config = await store.ReadAsync(actor.UserId, dek, row.ConfigBlobId, ct);
            items.Add(ToDto(row, config));
        }

        return new PagedResult<SourceDto>
        {
            Items = items,
            NextCursor = hasMore
                ? CursorPager.Encode(rows[^1].CreatedAt, rows[^1].Id)
                : null,
        };
    }

    /// <summary>
    /// <c>POST /v1/user/sources</c> — plugin + schema gate, then one
    /// transaction that writes the config blob, the
    /// <c>status='Active'</c> source row and the <c>create_source</c> audit row.
    /// </summary>
    public async Task<SourceDto> CreateAsync(
        SourceActor actor,
        byte[] dek,
        string typeId,
        string displayName,
        int? intervalMinutes,
        JsonElement config,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(dek);

        var plugin = ResolvePlugin(typeId);
        ValidateConfig(plugin, config);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var blobId = await store.WriteAsync(actor.UserId, dek, config, ct);
        var source = new MessageSource
        {
            Id = Guid.NewGuid(),
            UserId = actor.UserId,
            TypeId = plugin.TypeId,
            DisplayName = displayName.Trim(),
            ConfigBlobId = blobId,
            IntervalMinutes = intervalMinutes ?? DefaultIntervalMinutes,
            CheckpointJson = "{}",
            Status = MessageSourceStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.MessageSources.Add(source);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(
            ActorEvent(actor, AuditActions.CreateSource, source.Id, new Dictionary<string, object?>
            {
                ["type_id"] = source.TypeId,
                ["display_name"] = source.DisplayName,
                ["interval_minutes"] = source.IntervalMinutes,
            }),
            ct);

        await transaction.CommitAsync(ct);
        return ToDto(source, config);
    }

    /// <summary>
    /// <c>GET /v1/user/sources/{id}</c> — decrypts the config and projects it
    /// without the password. 404 when the row is not the caller's.
    /// </summary>
    public async Task<SourceDto> GetAsync(
        SourceActor actor, byte[] dek, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(dek);

        var source = await FindOwnedAsync(actor.UserId, id, ct);
        var config = await store.ReadAsync(actor.UserId, dek, source.ConfigBlobId, ct);
        return ToDto(source, config);
    }

    /// <summary>
    /// <c>PATCH /v1/user/sources/{id}</c> — optional <c>display_name</c>,
    /// <c>interval_minutes</c> and <c>config</c>. A config that omits the
    /// password (or sends <c>""</c>) inherits the stored one — the API never
    /// returns it, so a form that did not collect it must not be able to wipe
    /// it (task Risks). A new config is re-validated, re-encrypted into a NEW
    /// blob and the old blob is deleted in the same transaction (SQ-7).
    /// </summary>
    public async Task<SourceDto> UpdateAsync(
        SourceActor actor,
        byte[] dek,
        Guid id,
        string? displayName,
        int? intervalMinutes,
        JsonElement? config,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(dek);

        var source = await FindOwnedAsync(actor.UserId, id, ct);

        // Always read the stored document: it feeds the response's config
        // member and supplies the password the request did not carry.
        var storedConfig = await store.ReadAsync(actor.UserId, dek, source.ConfigBlobId, ct);
        var effectiveConfig = storedConfig;

        if (config is { } candidate)
        {
            effectiveConfig = MergeStoredPassword(candidate, storedConfig);
            ValidateConfig(ResolvePlugin(source.TypeId), effectiveConfig);
        }

        if (displayName is not null)
        {
            source.DisplayName = displayName.Trim();
        }

        if (intervalMinutes is { } interval)
        {
            source.IntervalMinutes = interval;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        if (config is not null)
        {
            var previousBlobId = source.ConfigBlobId;
            source.ConfigBlobId = await store.WriteAsync(actor.UserId, dek, effectiveConfig, ct);
            await db.SaveChangesAsync(ct);

            // Point the FK at the new blob first — the old row is FK-protected
            // until this save lands (Q-O15 / SQ-7, same transaction).
            await store.DeleteAsync(previousBlobId, ct);
        }
        else
        {
            await db.SaveChangesAsync(ct);
        }

        await audit.LogAsync(
            ActorEvent(actor, AuditActions.UpdateSource, source.Id, new Dictionary<string, object?>
            {
                ["changed_fields"] = ChangedFields(displayName, intervalMinutes, config),
            }),
            ct);

        await transaction.CommitAsync(ct);
        return ToDto(source, effectiveConfig);
    }

    /// <summary>
    /// <c>DELETE /v1/user/sources/{id}</c> — source row first, then its config
    /// blob, then the audit row, all in one transaction (SQ-7's source-driven
    /// cascade: no SQL <c>ON DELETE CASCADE</c>).
    /// </summary>
    /// <remarks>
    /// A source that already has <c>processed_messages</c> rows cannot be
    /// removed: §6/Q-O15 keeps those rows and their FK is <c>RESTRICT</c>, so
    /// deleting the parent would violate it (the relaxation that permits both
    /// lands with M6aT7's erasure cascade). The refusal is an explicit 409
    /// rather than a 500 from the database.
    /// </remarks>
    public async Task DeleteAsync(SourceActor actor, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var source = await FindOwnedAsync(actor.UserId, id, ct);

        if (await db.ProcessedMessages.AsNoTracking()
                .AnyAsync(message => message.SourceId == source.Id, ct))
        {
            throw new SourceHasMessagesException(source.Id);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var blobId = source.ConfigBlobId;
        db.MessageSources.Remove(source);
        await db.SaveChangesAsync(ct);
        await store.DeleteAsync(blobId, ct);

        await audit.LogAsync(
            ActorEvent(actor, AuditActions.DeleteSource, source.Id, new Dictionary<string, object?>
            {
                ["type_id"] = source.TypeId,
                ["display_name"] = source.DisplayName,
            }),
            ct);

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// <c>POST /v1/user/sources/test-connection</c> — validates against the
    /// plugin's schema FIRST (a <c>port</c> of 99999 is a §13.12 400 before any
    /// socket is opened) and only then runs the plugin's
    /// <c>ValidateConfigAsync</c>, whose network/auth failures come back as
    /// <c>{ ok: false, error }</c> with HTTP 200 (task Risks).
    /// </summary>
    public async Task<TestConnectionOutcome> TestConnectionAsync(
        SourceActor actor, string typeId, JsonElement config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var plugin = ResolvePlugin(typeId);
        ValidateConfig(plugin, config);

        ValidationResult result;
        try
        {
            result = await plugin.ValidateConfigAsync(config, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Credentials, transport and schema failures come back as Ok=false
            // (M2T4 contract). Anything else is the task's documented
            // /errors/source-connection-failed class, not a generic 500.
            throw new SourceConnectionFailedException(plugin.TypeId, exception);
        }

        var error = result.Ok
            ? null
            : string.IsNullOrWhiteSpace(result.Error)
                ? "The connection test failed."
                : result.Error;

        await audit.LogAsync(
            ActorEvent(actor, AuditActions.TestSourceConnection, targetId: null,
                new Dictionary<string, object?>
                {
                    ["type_id"] = plugin.TypeId,
                    ["ok"] = result.Ok,
                }),
            ct);

        return new TestConnectionOutcome(result.Ok, error);
    }

    /// <summary>
    /// <c>POST /v1/user/sources/{id}/fetch-now</c> — the M2 manual exerciser
    /// for the §10 fetch step: list messages newer than the checkpoint, write
    /// one dedupe-keyed <c>processed_messages</c> row (plus its encrypted body
    /// blob) per new message, advance the checkpoint and audit. No engine is
    /// involved anywhere — every column the M3 pipeline later fills stays NULL
    /// (<c>M2_NoLlmCall_Happens</c>).
    /// </summary>
    /// <remarks>
    /// Persistence runs <c>INSERT … ON CONFLICT (source_id, remote_uid) DO
    /// NOTHING</c> exactly as §10 prescribes, behind a cheap existence probe
    /// that keeps a re-run from leaving an orphan <c>message_body</c> blob: the
    /// blob is written only when the row is missing, and a lost race deletes it
    /// again before the transaction commits. <c>FetchOutcome.Processed</c> is
    /// what the connector yielded; <c>NewProcessedMessages</c> is what the
    /// database actually accepted.
    /// </remarks>
    public async Task<FetchOutcome> FetchNowAsync(
        SourceActor actor, byte[] dek, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(dek);

        var source = await FindOwnedAsync(actor.UserId, id, ct);
        var config = await store.ReadAsync(actor.UserId, dek, source.ConfigBlobId, ct);
        var plugin = ResolvePlugin(source.TypeId);

        // Q-F4: validation runs at save AND at execution time — a plugin
        // reload that tightened the schema must not reach the network.
        ValidateConfig(plugin, config);

        var checkpoint = ParseCheckpoint(source.CheckpointJson);
        List<RemoteMessage> messages = [];
        await foreach (var message in plugin.ListNewMessagesAsync(config, checkpoint, ct))
        {
            messages.Add(message);
        }

        var newProcessed = 0;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        foreach (var message in messages)
        {
            var alreadyStored = await db.ProcessedMessages.AsNoTracking()
                .AnyAsync(
                    row => row.SourceId == source.Id && row.RemoteUid == message.RemoteUid, ct);

            if (alreadyStored)
            {
                continue;
            }

            var bodyBlobId = await store.WriteBodyAsync(
                actor.UserId, dek, Encoding.UTF8.GetBytes(message.BodyText), ct);

            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO processed_messages
                    (id, source_id, remote_uid, received_remote_at, stage, prefilter_matched,
                     model_decision_json, actions_executed_json, prompt_tokens, completion_tokens,
                     total_tokens, cost_usd, body_blob_id, created_at)
                VALUES
                    ({Guid.NewGuid()}, {source.Id}, {message.RemoteUid}, {message.ReceivedRemoteAt},
                     {ProcessedMessageStage.fetched.ToString()}, {false},
                     NULL, NULL, NULL, NULL, NULL, NULL, {bodyBlobId}, now())
                ON CONFLICT (source_id, remote_uid) DO NOTHING
                """, ct);

            if (inserted > 0)
            {
                newProcessed++;
            }
            else
            {
                // The row was written concurrently — drop the blob we just made
                // so no unreferenced message_body blob survives the race.
                await store.DeleteAsync(bodyBlobId, ct);
            }
        }

        var advanced = await plugin.AdvanceCheckpointAsync(config, checkpoint, messages, ct);
        var now = DateTimeOffset.UtcNow;

        source.CheckpointJson = JsonSerializer.Serialize(advanced);
        source.LastRunAt = now;
        source.NextRunAt = now.AddMinutes(source.IntervalMinutes);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(
            ActorEvent(actor, AuditActions.FetchSourceNow, source.Id, new Dictionary<string, object?>
            {
                ["type_id"] = source.TypeId,
                ["processed"] = messages.Count,
                ["new_processed_messages"] = newProcessed,
                ["last_uid"] = advanced.LastUid,
            }),
            ct);

        await transaction.CommitAsync(ct);
        return new FetchOutcome(messages.Count, newProcessed, advanced);
    }

    /// <summary>Registry lookup; an unknown <c>type_id</c> is a §13.12 400.</summary>
    private IMessageSource ResolvePlugin(string typeId)
        => registry.GetMessageSource(typeId) ?? throw new PluginNotRegisteredException(typeId);

    /// <summary>
    /// The plugin's <c>ConfigSchema</c> gate (M2T2). Failure becomes
    /// <see cref="ConfigValidationException"/> with the field-level issues.
    /// </summary>
    private static void ValidateConfig(IMessageSource plugin, JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigValidationException(
                "The config member must be a JSON object.",
                new[] { new ValidationIssue(string.Empty, "type") },
                plugin.ConfigSchema);
        }

        var result = JsonSchemaValidator.Validate(plugin.ConfigSchema, config);
        if (!result.Ok)
        {
            throw new ConfigValidationException(
                result.Error, result.Issues ?? Array.Empty<ValidationIssue>(), plugin.ConfigSchema);
        }
    }

    /// <summary>
    /// Returns <paramref name="candidate"/> with the stored password filled in
    /// when the request omitted it or sent the empty string — the documented
    /// "leave unchanged" PATCH semantics (task Risks).
    /// </summary>
    private static JsonElement MergeStoredPassword(JsonElement candidate, JsonElement stored)
    {
        if (candidate.ValueKind != JsonValueKind.Object)
        {
            return candidate;
        }

        // Only a non-empty string counts as "the caller supplied a password";
        // an omitted member, an explicit null or "" all mean "keep the stored
        // one" — the JSON-Schema minLength: 1 would otherwise reject the empty
        // string the SPA is allowed to send (task Risks).
        var sentOwnPassword = false;
        if (candidate.TryGetProperty("password", out var password))
        {
            sentOwnPassword = password.ValueKind switch
            {
                JsonValueKind.String => !string.IsNullOrEmpty(password.GetString()),
                JsonValueKind.Null or JsonValueKind.Undefined => false,
                _ => true,
            };
        }

        if (sentOwnPassword)
        {
            return candidate.Clone();
        }

        if (stored.ValueKind != JsonValueKind.Object
            || !stored.TryGetProperty("password", out var storedPassword))
        {
            return candidate.Clone();
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in candidate.EnumerateObject())
            {
                if (property.NameEquals("password"))
                {
                    continue;
                }

                writer.WritePropertyName(property.Name);
                property.Value.WriteTo(writer);
            }

            writer.WritePropertyName("password");
            storedPassword.WriteTo(writer);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private async Task<MessageSource> FindOwnedAsync(Guid userId, Guid id, CancellationToken ct)
        => await db.MessageSources.FirstOrDefaultAsync(
                source => source.Id == id && source.UserId == userId, ct)
            ?? throw new SourceNotFoundException(id);

    private static SourceCheckpoint ParseCheckpoint(string checkpointJson)
    {
        using var document = JsonDocument.Parse(checkpointJson);
        return SourceCheckpoint.FromJson(document.RootElement);
    }

    /// <summary>
    /// The wire projection. The password is never read here —
    /// <see cref="SourceConfigDto"/> has no such member, so no response path
    /// can leak it (acceptance criterion: "no <c>password</c> field").
    /// </summary>
    private static SourceDto ToDto(MessageSource source, JsonElement config)
    {
        var checkpoint = ParseCheckpoint(source.CheckpointJson);

        return new SourceDto
        {
            Id = source.Id,
            TypeId = source.TypeId,
            DisplayName = source.DisplayName,
            Status = source.Status.ToString(),
            IntervalMinutes = source.IntervalMinutes,
            Checkpoint = new SourceCheckpointDto
            {
                LastUid = checkpoint.LastUid,
                Extra = checkpoint.Extra,
            },
            LastRunAt = source.LastRunAt,
            NextRunAt = source.NextRunAt,
            CreatedAt = source.CreatedAt,
            Config = SourceConfigDto.FromJson(config),
        };
    }

    private static List<string> ChangedFields(
        string? displayName, int? intervalMinutes, JsonElement? config)
    {
        var changed = new List<string>(3);
        if (displayName is not null)
        {
            changed.Add("display_name");
        }

        if (intervalMinutes is not null)
        {
            changed.Add("interval_minutes");
        }

        if (config is not null)
        {
            changed.Add("config");
        }

        return changed;
    }

    /// <summary>
    /// The §15 audit row for a source event — actor from the caller, target is
    /// the source (when it has one). Metadata carries field names and counts
    /// only: never a config document and never a credential.
    /// </summary>
    private static AuditEvent ActorEvent(
        SourceActor actor,
        string action,
        Guid? targetId,
        IReadOnlyDictionary<string, object?> metadata)
        => new(
            ActorUserId: actor.UserId,
            ActorRole: actor.Role,
            Action: action,
            TargetType: "source",
            TargetId: targetId?.ToString(),
            MetadataJson: JsonSerializer.Serialize(metadata),
            Ip: actor.Ip);
}
