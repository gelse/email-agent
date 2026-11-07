using EmailAgent.Api.Endpoints;
using EmailAgent.Api.Pagination;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Enums;
using Microsoft.EntityFrameworkCore;

namespace EmailAgent.Api.Models;

/// <summary>
/// M3T7 / SQ-M3-6 + SQ-M3-12: the read-only allow-list query behind
/// <c>GET /v1/user/models</c>. The filter is enforced HERE — server side, in
/// one place — so the SPA can never widen it: exactly
/// <c>enabled = true AND tool_calling_probe_result = 'Pass'</c> (§11's model
/// allow-list), projected down to the reduced <see cref="UserModelDto"/>.
/// </summary>
/// <remarks>
/// Read-only: no persistence, no audit rows (Q-API44/M2T5 list parity — reads
/// are not state changes), no decryption of any kind. Scoped like
/// <see cref="ModelService"/> so it shares the request's
/// <see cref="EmailAgentDbContext"/>.
/// </remarks>
internal sealed class UserModelService
{
    private readonly EmailAgentDbContext _db;

    public UserModelService(EmailAgentDbContext db)
        => _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <summary>
    /// One capped page of pickable models ordered <c>(created_at, id)</c> so
    /// the picker is deterministic across calls (the tie-break only matters
    /// when two rows share a write tick — same rationale as the admin list's
    /// <c>(created_at DESC, id DESC)</c>).
    /// </summary>
    /// <param name="limit">§13.13 page size — default 25, max 100.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <exception cref="ArgumentOutOfRangeException">The limit is outside 1..100.</exception>
    public async Task<IReadOnlyList<UserModelDto>> ListPickableAsync(
        int limit, CancellationToken ct)
    {
        if (limit < PaginationQuery.MinLimit || limit > PaginationQuery.MaxLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                $"The page size must be between {PaginationQuery.MinLimit} and {PaginationQuery.MaxLimit}.");
        }

        // The allow-list: enabled AND Pass. Nothing else can reach the picker.
        //
        // Order + cap are the M3 contract: ASC (created_at, id), default 25 /
        // max 100, and NO cursor (§13.13 defines no next_cursor here). Once
        // more rows are pickable than one page holds, Take therefore keeps the
        // OLDEST N and drops the newer ones — a deliberate M3 truncation, to be
        // replaced by cursor paging when M3 pagination arrives.
        var rows = await _db.ModelCatalog
            .AsNoTracking()
            .Where(model => model.Enabled
                         && model.ToolCallingProbeResult == ToolCallingProbeResult.Pass)
            .OrderBy(model => model.CreatedAt)
            .ThenBy(model => model.Id)
            .Take(limit)
            .ToListAsync(ct);

        return rows.Select(model => new UserModelDto
        {
            Id = model.Id,
            DisplayName = model.DisplayName,
            Provider = model.Provider,
            ModelName = model.ModelName,
        }).ToList();
    }
}
