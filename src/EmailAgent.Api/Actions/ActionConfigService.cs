using System.Net;
using System.Text.Json;
using EmailAgent.Api.Pagination;
using EmailAgent.Api.Sources;
using EmailAgent.Contracts;
using EmailAgent.Contracts.Schema;
using EmailAgent.Core.Audit;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Plugins;
using Microsoft.EntityFrameworkCore;

namespace EmailAgent.Api.Actions;

public sealed record ActionActor(Guid UserId, string Role, IPAddress? Ip);

/// <summary>
/// M4T3: action-config CRUD + notification reads/updates. Ownership is
/// enforced on every lookup (foreign id → 404, never 403). Config
/// read/write requires the M2–M4 interim <c>X-User-Dek</c> (Q-E9).
/// </summary>
public sealed class ActionConfigService
{
    private readonly EmailAgentDbContext _db;
    private readonly IPluginRegistry _registry;
    private readonly EncryptedActionConfigStore _store;
    private readonly IAuditLogger _audit;

    public ActionConfigService(
        EmailAgentDbContext db,
        IPluginRegistry registry,
        EncryptedActionConfigStore store,
        IAuditLogger audit)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public async Task<PagedResult<ActionConfigRow>> ListAsync(
        ActionActor actor, byte[] dek, string? typeId, string? cursor, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(dek);

        IQueryable<ActionConfig> query = _db.ActionConfigs
            .AsNoTracking()
            .Where(a => a.UserId == actor.UserId);

        if (!string.IsNullOrEmpty(typeId))
        {
            query = query.Where(a => a.TypeId == typeId);
        }

        var rows = await CursorPager.ApplyCursor(query, cursor, limit).ToListAsync(ct);
        var hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);

        var items = new List<ActionConfigRow>(rows.Count);
        foreach (var row in rows)
        {
            var config = await _store.ReadAsync(actor.UserId, dek, row.ConfigBlobId, ct).ConfigureAwait(false);
            items.Add(new ActionConfigRow(row, config));
        }

        return new PagedResult<ActionConfigRow>
        {
            Items = items,
            NextCursor = hasMore ? CursorPager.Encode(rows[^1].CreatedAt, rows[^1].Id) : null,
        };
    }

    public async Task<ActionConfigRow> CreateAsync(
        ActionActor actor,
        byte[] dek,
        string typeId,
        string displayName,
        bool? requireConfirmation,
        bool? dryRunDefault,
        JsonElement config,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(dek);

        var handler = _registry.GetActionHandler(typeId)
            ?? throw new ActionPluginNotRegisteredException(typeId);
        ValidateConfig(handler.ConfigSchema, config);

        await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var blobId = await _store.WriteAsync(actor.UserId, dek, config, ct).ConfigureAwait(false);
        var row = new ActionConfig
        {
            Id = Guid.NewGuid(),
            UserId = actor.UserId,
            TypeId = typeId,
            DisplayName = displayName.Trim(),
            ConfigBlobId = blobId,
            RequireConfirmation = requireConfirmation ?? true,
            DryRunDefault = dryRunDefault ?? false,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.ActionConfigs.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _audit.LogAsync(ActorEvent(actor, AuditActions.CreateAction, row.Id, new Dictionary<string, object?>
        {
            ["type_id"] = row.TypeId,
            ["display_name"] = row.DisplayName,
            ["require_confirmation"] = row.RequireConfirmation,
            ["dry_run_default"] = row.DryRunDefault,
        }), ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new ActionConfigRow(row, config);
    }

    public async Task<ActionConfigRow> GetAsync(ActionActor actor, byte[] dek, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(dek);
        var row = await FindOwnedAsync(actor.UserId, id, ct).ConfigureAwait(false);
        var config = await _store.ReadAsync(actor.UserId, dek, row.ConfigBlobId, ct).ConfigureAwait(false);
        return new ActionConfigRow(row, config);
    }

    public async Task<ActionConfigRow> UpdateAsync(
        ActionActor actor,
        byte[] dek,
        Guid id,
        string? displayName,
        bool? requireConfirmation,
        bool? dryRunDefault,
        JsonElement? config,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(dek);

        var row = await FindOwnedAsync(actor.UserId, id, ct).ConfigureAwait(false);
        var storedConfig = await _store.ReadAsync(actor.UserId, dek, row.ConfigBlobId, ct).ConfigureAwait(false);
        var effectiveConfig = storedConfig;

        if (config is { } candidate)
        {
            effectiveConfig = MergeStoredPassword(candidate, storedConfig);
            var handler = _registry.GetActionHandler(row.TypeId)
                ?? throw new ActionPluginNotRegisteredException(row.TypeId);
            ValidateConfig(handler.ConfigSchema, effectiveConfig);
        }

        if (displayName is not null) row.DisplayName = displayName.Trim();
        if (requireConfirmation.HasValue) row.RequireConfirmation = requireConfirmation.Value;
        if (dryRunDefault.HasValue) row.DryRunDefault = dryRunDefault.Value;

        await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        if (config is not null)
        {
            var previousBlobId = row.ConfigBlobId;
            row.ConfigBlobId = await _store.WriteAsync(actor.UserId, dek, effectiveConfig, ct).ConfigureAwait(false);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            await _store.DeleteAsync(previousBlobId, ct).ConfigureAwait(false);
        }
        else
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        var changed = new List<string>();
        if (displayName is not null) changed.Add("display_name");
        if (requireConfirmation.HasValue) changed.Add("require_confirmation");
        if (dryRunDefault.HasValue) changed.Add("dry_run_default");
        if (config is not null) changed.Add("config");

        await _audit.LogAsync(ActorEvent(actor, AuditActions.UpdateAction, row.Id, new Dictionary<string, object?>
        {
            ["changed_fields"] = changed,
        }), ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new ActionConfigRow(row, effectiveConfig);
    }

    public async Task DeleteAsync(ActionActor actor, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var row = await FindOwnedAsync(actor.UserId, id, ct).ConfigureAwait(false);

        var inUse = await _db.PendingActions.AsNoTracking()
            .AnyAsync(p => p.ActionConfigId == row.Id, ct).ConfigureAwait(false);
        if (inUse)
        {
            throw new ActionInUseException(row.Id);
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var blobId = row.ConfigBlobId;
        _db.ActionConfigs.Remove(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await _store.DeleteAsync(blobId, ct).ConfigureAwait(false);

        await _audit.LogAsync(ActorEvent(actor, AuditActions.DeleteAction, row.Id, new Dictionary<string, object?>
        {
            ["type_id"] = row.TypeId,
            ["display_name"] = row.DisplayName,
        }), ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private async Task<ActionConfig> FindOwnedAsync(Guid userId, Guid id, CancellationToken ct)
        => await _db.ActionConfigs.FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId, ct).ConfigureAwait(false)
            ?? throw new ActionConfigNotFoundException(id);

    private static void ValidateConfig(Json.Schema.JsonSchema schema, JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object)
        {
            throw new ActionConfigValidationException(
                "The config member must be a JSON object.",
                [new ValidationIssue(string.Empty, "type")],
                schema);
        }

        var result = JsonSchemaValidator.Validate(schema, config);
        if (!result.Ok)
        {
            throw new ActionConfigValidationException(
                result.Error, result.Issues ?? Array.Empty<ValidationIssue>(), schema);
        }
    }

    /// <summary>
    /// Mirrors M2T5's "leave unchanged" PATCH semantics for <c>password</c>:
    /// omitted/empty/null preserves the stored password so the SPA never has to
    /// resend it. Generic across action config schemas.
    /// </summary>
    private static JsonElement MergeStoredPassword(JsonElement candidate, JsonElement stored)
    {
        if (candidate.ValueKind != JsonValueKind.Object) return candidate.Clone();

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

        if (sentOwnPassword) return candidate.Clone();
        if (stored.ValueKind != JsonValueKind.Object || !stored.TryGetProperty("password", out var storedPassword))
        {
            return candidate.Clone();
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in candidate.EnumerateObject())
            {
                if (property.NameEquals("password")) continue;
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

    private static AuditEvent ActorEvent(
        ActionActor actor, string action, Guid? targetId, IReadOnlyDictionary<string, object?> metadata)
        => new(actor.UserId, actor.Role, action, "action_config", targetId?.ToString(),
            JsonSerializer.Serialize(metadata), actor.Ip);
}

/// <summary>Service-layer projection carrying the decrypted config (pre-DK strip).</summary>
public sealed record ActionConfigRow(ActionConfig Entity, JsonElement Config);
