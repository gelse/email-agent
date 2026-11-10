using System.Net;
using System.Text.Json;
using EmailAgent.Api.Endpoints;
using EmailAgent.Api.Pagination;
using EmailAgent.Api.Sources;
using EmailAgent.Contracts;
using EmailAgent.Contracts.Schema;
using EmailAgent.Connectors.OpenAI;
using EmailAgent.Core.Audit;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using Microsoft.EntityFrameworkCore;

namespace EmailAgent.Api.Models;

/// <summary>The authenticated admin as the audit layer needs it: identity, role and client address.</summary>
public sealed record ModelActor(Guid UserId, string Role, IPAddress? Ip);

/// <summary>No <c>model_catalog</c> row with that id — §13.12 404 <c>/errors/model-not-found</c>.</summary>
public sealed class ModelNotFoundException : Exception
{
    public ModelNotFoundException(Guid modelId)
        : base($"No model with id {modelId}.") => ModelId = modelId;

    public Guid ModelId { get; }
}

/// <summary>
/// The M3T2 probe rejected the model configuration (SQ-M3-11 /
/// SQ-M3-3): a non-recoverable §13.12 400 <c>/errors/model-probe-failed</c>.
/// The probe has already emitted its fail-only <c>ModelProbeFailed</c> audit
/// row, so this exception carries only the reason and the ≤ 256-byte response
/// excerpt the problem document surfaces as <c>detail</c> — never a key.
/// </summary>
public sealed class ModelProbeFailedException : Exception
{
    public ModelProbeFailedException(string? reason, string responseExcerpt)
        : base($"The model probe failed ({reason ?? "unknown reason"}).")
    {
        Reason = reason;
        ResponseExcerpt = responseExcerpt ?? string.Empty;
    }

    /// <summary><c>no_tool_calls</c> | <c>unexpected_tool_names</c>.</summary>
    public string? Reason { get; }

    /// <summary>The reconstructed response body, already clamped to 256 UTF-8 bytes by M3T2.</summary>
    public string ResponseExcerpt { get; }
}

/// <summary>
/// M3T3: the five <c>/v1/admin/models</c> operations (task scope table).
/// Ownership is the admin namespace itself (no per-user scoping), so this
/// service owns ordering/filters, the schema gate, the mandatory probe gate,
/// system-key encryption, transactional persistence and the three Q-O17 audit
/// rows; the endpoint layer only translates HTTP (RFC 7807 documents, query
/// parsing, actor resolution).
/// </summary>
/// <remarks>
/// <para>
/// <b>Probe before write (SQ-M3-11).</b> Create and Update validate the
/// engine-config subset against M3T1's schema, then run the probe BEFORE any
/// catalog/blob mutation: a <c>Fail</c> throws
/// <see cref="ModelProbeFailedException"/> with no row written and no field
/// updated — the probe's own fail-only <c>ModelProbeFailed</c> audit row is
/// the only write. The entity is mutated strictly after the probe returns,
/// because the probe's fail path appends its audit row through the shared
/// scoped <see cref="EmailAgentDbContext"/> (a pending change would flush with
/// it).
/// </para>
/// <para>
/// <b>Transactions.</b> Create, Update and Delete each open one transaction
/// that also carries the audit row, so catalog row, key blob and evidence
/// commit together or not at all (the M2T5/§7 pattern applied to model
/// lifecycle).
/// </para>
/// <para>
/// <b>Internal on purpose:</b> the ctor takes M3T2's internal
/// <see cref="IModelProbeService"/> (CS0051 would otherwise reject the public
/// member — the same reason the probe seam itself is internal, M3T2 deviation
/// D3). The endpoint layer and the friend test suites consume it in-assembly.
/// </para>
/// </remarks>
internal sealed class ModelService(
    EmailAgentDbContext db,
    IModelProbeService probe,
    EncryptedModelApiKeyStore store,
    IAuditLogger audit,
    TimeProvider clock)
{
    /// <summary><c>audit_log.target_type</c> / probe target type for catalog events.</summary>
    private const string CatalogTargetType = "model_catalog";

    /// <summary>
    /// <c>GET /v1/admin/models</c> — one keyset page of the catalog
    /// (§13.13, SQ-M3-2 order <c>(created_at DESC, id DESC)</c>) with the
    /// <c>enabled</c>, <c>provider</c> and <c>display_name_contains</c>
    /// filters. Reads are not audited (Q-API44 — the M2T5 list pattern).
    /// </summary>
    public async Task<PagedResult<ModelDto>> ListAsync(
        bool? enabled,
        string? provider,
        string? displayNameContains,
        string? cursor,
        int limit,
        CancellationToken ct)
    {
        if (limit < PaginationQuery.MinLimit || limit > PaginationQuery.MaxLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                $"The page size must be between {PaginationQuery.MinLimit} and {PaginationQuery.MaxLimit}.");
        }

        // Read-only projection: no need to track up to 100 rows per page.
        IQueryable<ModelCatalogEntry> query = db.ModelCatalog.AsNoTracking();

        if (enabled is { } enabledValue)
        {
            query = query.Where(model => model.Enabled == enabledValue);
        }

        if (!string.IsNullOrEmpty(provider))
        {
            query = query.Where(model => model.Provider == provider);
        }

        if (!string.IsNullOrEmpty(displayNameContains))
        {
            query = query.Where(model => model.DisplayName.Contains(displayNameContains));
        }

        if (!string.IsNullOrEmpty(cursor))
        {
            // Strict decode: a malformed cursor is a §13.12 400, never a 500.
            var (createdAt, id) = Cursor.Decode(cursor);
            query = query.Where(model => model.CreatedAt < createdAt
                || (model.CreatedAt == createdAt && model.Id < id));
        }

        // limit + 1 rows: the extra row proves a next page exists without a
        // second COUNT query.
        var rows = await query
            .OrderByDescending(model => model.CreatedAt)
            .ThenByDescending(model => model.Id)
            .Take(limit + 1)
            .ToListAsync(ct);

        var hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return new PagedResult<ModelDto>
        {
            Items = rows.Select(ToDto).ToList(),
            NextCursor = hasMore ? Cursor.Encode(rows[^1].CreatedAt, rows[^1].Id) : null,
        };
    }

    /// <summary><c>GET /v1/admin/models/{id}</c> — 404 <c>/errors/model-not-found</c> when unknown.</summary>
    public async Task<ModelDto> GetAsync(Guid id, CancellationToken ct)
    {
        var entry = await db.ModelCatalog.AsNoTracking()
                .FirstOrDefaultAsync(model => model.Id == id, ct)
            ?? throw new ModelNotFoundException(id);
        return ToDto(entry);
    }

    /// <summary>
    /// <c>POST /v1/admin/models</c> — schema gate, then the mandatory probe
    /// (SQ-M3-11: BEFORE any database write), then one transaction carrying
    /// the system-wrapped key blob, the catalog row and the
    /// <c>create_model</c> audit row. 201 with a key-free <see cref="ModelDto"/>.
    /// </summary>
    public async Task<ModelDto> CreateAsync(
        CreateModelRequest request, ModelActor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        var baseUrl = request.BaseUrl?.Trim();
        var modelName = request.ModelName?.Trim();
        var apiKey = request.ApiKey;

        // §13.12 400 /errors/config-validation before any socket or write.
        ValidateEngineConfig(baseUrl, apiKey, modelName);

        // Probe BEFORE any write (SQ-M3-11). On Fail the probe already emitted
        // its ModelProbeFailed row (target_id NULL on this path) — that row is
        // the ONLY write; no model_catalog / encrypted_blobs row exists.
        var probeResult = await probe.ProbeAsync(
            new OpenAICompatibleConfig(baseUrl!, apiKey!, modelName!),
            correlationModelId: null,
            ct);
        if (probeResult.Outcome == ProbeOutcome.Fail)
        {
            throw new ModelProbeFailedException(probeResult.Reason, probeResult.ResponseExcerpt);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var blobId = await store.WriteAsync(apiKey!, ct);
        var entry = new ModelCatalogEntry
        {
            Id = Guid.NewGuid(),
            DisplayName = request.DisplayName!.Trim(),
            Provider = request.Provider!.Trim(),
            BaseUrl = baseUrl!,
            ModelName = modelName!,
            ApiKeySecretRef = blobId.ToString(),
            PricePer1kInputUsd = request.PricePer1kInputUsd,
            PricePer1kOutputUsd = request.PricePer1kOutputUsd,
            Enabled = request.Enabled ?? true,
            CreatedBy = actor.UserId,
            CreatedAt = clock.GetUtcNow(),
            // Pass and PassFromCache are both a passing gate (task scope table);
            // the probe result's ProbedAt is this call's timestamp.
            ToolCallingProbedAt = probeResult.ProbedAt,
            ToolCallingProbeResult = ToolCallingProbeResult.Pass,
        };

        db.ModelCatalog.Add(entry);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(
            ActorEvent(
                actor, AuditActions.CreateModel, entry.Id,
                new Dictionary<string, object?>
                {
                    ["model_id"] = entry.Id,
                    ["display_name"] = entry.DisplayName,
                    ["provider"] = entry.Provider,
                    ["base_url"] = entry.BaseUrl,
                    ["model_name"] = entry.ModelName,
                    ["probed_at"] = probeResult.ProbedAt,
                    ["probe_outcome"] = probeResult.Outcome.ToString(),
                }),
            ct);

        await transaction.CommitAsync(ct);
        return ToDto(entry);
    }

    /// <summary>
    /// <c>PATCH /v1/admin/models/{id}</c> — partial update that re-invokes the
    /// probe on EVERY call (SQ-M3-13: a within-window no-change patch is served
    /// <c>PassFromCache</c> with no HTTP call; a changed
    /// <c>base_url</c>/<c>model_name</c> or an expired window re-probes). A
    /// supplied <c>api_key</c> is re-encrypted into a NEW blob with the old one
    /// deleted in the same transaction; on probe fail NO field is updated.
    /// </summary>
    public async Task<ModelDto> UpdateAsync(
        Guid id, UpdateModelRequest request, ModelActor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        // Loaded (but not yet mutated) so a probe failure can prove "no fields
        // are updated" — and so the fail path's audit write cannot flush a
        // pending change through the shared scoped context.
        var entry = await db.ModelCatalog.FirstOrDefaultAsync(
                model => model.Id == id, ct)
            ?? throw new ModelNotFoundException(id);

        var baseUrl = request.BaseUrl is null ? entry.BaseUrl : request.BaseUrl.Trim();
        var modelName = request.ModelName is null ? entry.ModelName : request.ModelName.Trim();

        // The effective config always carries a key: the patch's own key, or
        // the stored one (the API never returns it, so a patch that omits it
        // must not be forced to re-send it — and the probe still needs it).
        var apiKey = request.ApiKey;
        if (apiKey is null)
        {
            if (entry.ApiKeySecretRef is not { Length: > 0 } storedRef
                || !Guid.TryParse(storedRef, out var storedBlobId))
            {
                // No stored key to fall back on: an unprovable engine config is
                // a 400 config-validation, not a probe over a keyless request.
                throw new ConfigValidationException(
                    "The stored model has no API key — supply api_key in the patch.",
                    new[] { new ValidationIssue("/api_key", "required") },
                    OpenAICompatibleConfigSchema.Build());
            }

            apiKey = await store.ReadAsync(storedBlobId, ct);
        }

        ValidateEngineConfig(baseUrl, apiKey, modelName);

        // Probe BEFORE mutating (SQ-M3-11 / SQ-M3-13): every PATCH probes,
        // correlation id = the row so the fail row targets it.
        var probeResult = await probe.ProbeAsync(
            new OpenAICompatibleConfig(baseUrl, apiKey, modelName),
            correlationModelId: id,
            ct);
        if (probeResult.Outcome == ProbeOutcome.Fail)
        {
            throw new ModelProbeFailedException(probeResult.Reason, probeResult.ResponseExcerpt);
        }

        var changedFields = ChangedFields(request);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        if (request.ApiKey is not null)
        {
            // Key rotation: write the new blob, point the ref at it, then drop
            // the old row — all inside this transaction (task acceptance
            // criterion; no FK on the soft ref, so the order is free).
            var newBlobId = await store.WriteAsync(request.ApiKey, ct);
            var previousBlobId = entry.ApiKeySecretRef is not null
                && Guid.TryParse(entry.ApiKeySecretRef, out var oldBlobId)
                ? oldBlobId
                : (Guid?)null;

            entry.ApiKeySecretRef = newBlobId.ToString();
            await db.SaveChangesAsync(ct);

            if (previousBlobId is { } blobToDelete)
            {
                await store.DeleteAsync(blobToDelete, ct);
            }
        }

        if (request.DisplayName is not null)
        {
            entry.DisplayName = request.DisplayName.Trim();
        }

        if (request.Provider is not null)
        {
            entry.Provider = request.Provider.Trim();
        }

        if (request.BaseUrl is not null)
        {
            entry.BaseUrl = baseUrl;
        }

        if (request.ModelName is not null)
        {
            entry.ModelName = modelName;
        }

        if (request.PricePer1kInputUsd is { } priceInput)
        {
            entry.PricePer1kInputUsd = priceInput;
        }

        if (request.PricePer1kOutputUsd is { } priceOutput)
        {
            entry.PricePer1kOutputUsd = priceOutput;
        }

        if (request.Enabled is { } enabled)
        {
            entry.Enabled = enabled;
        }

        // Refresh from the probe result (task step 3): Pass and PassFromCache
        // are both a passing gate, so the row keeps a live Pass.
        entry.ToolCallingProbedAt = probeResult.ProbedAt;
        entry.ToolCallingProbeResult = ToolCallingProbeResult.Pass;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(
            ActorEvent(
                actor, AuditActions.UpdateModel, entry.Id,
                new Dictionary<string, object?>
                {
                    ["model_id"] = entry.Id,
                    ["changed_fields"] = changedFields,
                    ["probed_at"] = probeResult.ProbedAt,
                    ["probe_outcome"] = probeResult.Outcome.ToString(),
                }),
            ct);

        await transaction.CommitAsync(ct);
        return ToDto(entry);
    }

    /// <summary>
    /// <c>DELETE /v1/admin/models/{id}</c> — catalog row, referenced key blob
    /// and the <c>delete_model</c> audit row in ONE transaction (Q-O15's
    /// source pattern applied to model lifecycle). 204 on success.
    /// </summary>
    public async Task DeleteAsync(Guid id, ModelActor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var entry = await db.ModelCatalog.FirstOrDefaultAsync(
                model => model.Id == id, ct)
            ?? throw new ModelNotFoundException(id);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var blobId = entry.ApiKeySecretRef is not null
            && Guid.TryParse(entry.ApiKeySecretRef, out var parsedBlobId)
            ? parsedBlobId
            : (Guid?)null;

        db.ModelCatalog.Remove(entry);
        await db.SaveChangesAsync(ct);

        if (blobId is { } keyBlobId)
        {
            await store.DeleteAsync(keyBlobId, ct);
        }

        await audit.LogAsync(
            ActorEvent(
                actor, AuditActions.DeleteModel, entry.Id,
                new Dictionary<string, object?>
                {
                    ["model_id"] = entry.Id,
                    ["display_name"] = entry.DisplayName,
                    ["provider"] = entry.Provider,
                }),
            ct);

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// The M3T1 engine-config gate (task step 3): the
    /// <c>base_url</c>/<c>api_key</c>/<c>model_name</c> subset is validated
    /// against <c>OpenAICompatibleConfigSchema</c> so a bad endpoint or empty
    /// key is a §13.12 400 <c>/errors/config-validation</c> before the probe
    /// ever opens a socket. Absent members are omitted from the instance, so
    /// the schema's own <c>required</c> keyword blames them.
    /// </summary>
    private static void ValidateEngineConfig(string? baseUrl, string? apiKey, string? modelName)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (baseUrl is not null)
            {
                writer.WriteString("base_url", baseUrl);
            }

            if (apiKey is not null)
            {
                writer.WriteString("api_key", apiKey);
            }

            if (modelName is not null)
            {
                writer.WriteString("model_name", modelName);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        var result = JsonSchemaValidator.Validate(
            OpenAICompatibleConfigSchema.Build(), document.RootElement);
        if (!result.Ok)
        {
            throw new ConfigValidationException(
                result.Error,
                result.Issues ?? Array.Empty<ValidationIssue>(),
                OpenAICompatibleConfigSchema.Build());
        }
    }

    /// <summary>
    /// The members the PATCH body named (fixed wire order) — the
    /// <c>update_model</c> audit row's <c>changed_fields</c> array. Present
    /// members are listed even when the value is unchanged (parity with M2T5's
    /// source update audit, which lists the members the request targeted).
    /// </summary>
    private static List<string> ChangedFields(UpdateModelRequest request)
    {
        var changed = new List<string>(8);
        if (request.DisplayName is not null)
        {
            changed.Add("display_name");
        }

        if (request.Provider is not null)
        {
            changed.Add("provider");
        }

        if (request.BaseUrl is not null)
        {
            changed.Add("base_url");
        }

        if (request.ModelName is not null)
        {
            changed.Add("model_name");
        }

        if (request.ApiKey is not null)
        {
            changed.Add("api_key");
        }

        if (request.PricePer1kInputUsd.HasValue)
        {
            changed.Add("price_per_1k_input_usd");
        }

        if (request.PricePer1kOutputUsd.HasValue)
        {
            changed.Add("price_per_1k_output_usd");
        }

        if (request.Enabled.HasValue)
        {
            changed.Add("enabled");
        }

        return changed;
    }

    /// <summary>
    /// The wire projection: the fixed 12-member <see cref="ModelDto"/> — no
    /// <c>api_key</c>, no <c>api_key_secret_ref</c>, no
    /// <c>encrypted_blob_id</c> (task "ModelDto projection"), so no response
    /// path can reach the credential.
    /// </summary>
    private static ModelDto ToDto(ModelCatalogEntry entry) => new()
    {
        Id = entry.Id,
        DisplayName = entry.DisplayName,
        Provider = entry.Provider,
        BaseUrl = entry.BaseUrl,
        ModelName = entry.ModelName,
        Enabled = entry.Enabled,
        ToolCallingProbedAt = entry.ToolCallingProbedAt,
        ToolCallingProbeResult = entry.ToolCallingProbeResult?.ToString(),
        CreatedBy = entry.CreatedBy,
        CreatedAt = entry.CreatedAt,
        PricePer1kInputUsd = entry.PricePer1kInputUsd,
        PricePer1kOutputUsd = entry.PricePer1kOutputUsd,
    };

    /// <summary>
    /// The §15 audit row for a catalog event — actor from the caller, target
    /// the catalog row. Metadata carries field names, the catalog's own values
    /// and the probe outcome: never the api_key (plaintext or wrapped).
    /// </summary>
    private static AuditEvent ActorEvent(
        ModelActor actor,
        string action,
        Guid? targetId,
        IReadOnlyDictionary<string, object?> metadata)
        => new(
            ActorUserId: actor.UserId,
            ActorRole: actor.Role,
            Action: action,
            TargetType: CatalogTargetType,
            TargetId: targetId?.ToString(),
            MetadataJson: JsonSerializer.Serialize(metadata),
            Ip: actor.Ip);
}
