using EmailAgent.Infrastructure.Blobs;
using Microsoft.EntityFrameworkCore;

namespace EmailAgent.Infrastructure.Actions;

/// <summary>
/// M4T1: resolves <c>(user_id, type_id)</c> to the most recent action config
/// (<c>created_at DESC, id DESC</c>, SQ-M4-8), enforces blob ownership +
/// purpose, and decrypts via <see cref="IUserBlobCrypto"/>. Returns
/// <c>null</c> when no row exists (the dispatcher records
/// <c>ConfigNotFound</c>).
/// </summary>
public sealed class DecryptedActionConfigProvider : IActionConfigProvider
{
    private readonly EmailAgentDbContext _db;
    private readonly IUserBlobCrypto _crypto;

    public DecryptedActionConfigProvider(EmailAgentDbContext db, IUserBlobCrypto crypto)
    {
        _db = db;
        _crypto = crypto;
    }

    /// <inheritdoc />
    public async Task<ActionConfigEnvelope?> ResolveAsync(
        Guid userId, string typeId, ReadOnlyMemory<byte> userDek, CancellationToken ct)
    {
        // SQ-M4-8: deterministic tiebreak when multiple configs share a type.
        var row = await _db.ActionConfigs
            .AsNoTracking()
            .Where(a => a.UserId == userId && a.TypeId == typeId)
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .Select(a => new
            {
                a.Id, a.TypeId, a.DisplayName, a.RequireConfirmation, a.DryRunDefault, a.ConfigBlobId,
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        var blob = await _db.EncryptedBlobs
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == row.ConfigBlobId, ct)
            .ConfigureAwait(false);

        if (blob is null
            || blob.OwnerUserId != userId
            || !string.Equals(blob.Purpose, "action_config", StringComparison.Ordinal))
        {
            return null;
        }

        var config = _crypto.Decrypt(userId, blob.Ciphertext, blob.Nonce, userDek, ct);
        return new ActionConfigEnvelope(
            row.Id, row.TypeId, row.DisplayName, row.RequireConfirmation, row.DryRunDefault, config);
    }
}
