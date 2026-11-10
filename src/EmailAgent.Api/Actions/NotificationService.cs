using System.Net;
using EmailAgent.Api.Pagination;
using EmailAgent.Api.Sources;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace EmailAgent.Api.Actions;

public sealed record NotificationActor(Guid UserId, string Role, IPAddress? Ip);

/// <summary>M4T3: notification list/read/read-all (ownership-scoped).</summary>
public sealed class NotificationService(EmailAgentDbContext db)
{
    public async Task<PagedResult<UserNotification>> ListAsync(
        NotificationActor actor, bool? unreadOnly, string? cursor, int limit, CancellationToken ct)
    {
        IQueryable<UserNotification> query = db.UserNotifications
            .AsNoTracking()
            .Where(n => n.UserId == actor.UserId);

        if (unreadOnly == true)
        {
            query = query.Where(n => n.ReadAt == null);
        }

        var rows = await CursorPager.ApplyCursor(query, cursor, limit).ToListAsync(ct);
        var hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);

        return new PagedResult<UserNotification>
        {
            Items = rows,
            NextCursor = hasMore ? CursorPager.Encode(rows[^1].CreatedAt, rows[^1].Id) : null,
        };
    }

    public async Task<UserNotification> MarkReadAsync(NotificationActor actor, Guid id, CancellationToken ct)
    {
        var row = await db.UserNotifications
            .FirstOrDefaultAsync(n => n.Id == id && n.UserId == actor.UserId, ct)
            .ConfigureAwait(false)
            ?? throw new NotificationNotFoundException(id);

        if (row.ReadAt is null)
        {
            row.ReadAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return row;
    }

    public async Task<int> MarkAllReadAsync(NotificationActor actor, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await db.UserNotifications
            .Where(n => n.UserId == actor.UserId && n.ReadAt == null)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var row in rows)
        {
            row.ReadAt = now;
        }

        if (rows.Count > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return rows.Count;
    }
}
