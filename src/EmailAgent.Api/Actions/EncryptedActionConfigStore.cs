using System.Security.Cryptography;
using System.Text.Json;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Blobs;
using EmailAgent.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace EmailAgent.Api.Actions;

/// <summary>
/// M4T3: writer/reader for action-config <c>encrypted_blobs</c> rows
/// (<c>purpose='action_config'</c>), backed by <see cref="IUserBlobCrypto"/>
/// (shared with the dispatcher reader — SQ-M4-8 / M4T1). Every method joins
/// the ambient transaction of the caller's scoped <see cref="EmailAgentDbContext"/>.
/// </summary>
public sealed class EncryptedActionConfigStore
{
    public const string Purpose = "action_config";

    private readonly EmailAgentDbContext _db;
    private readonly IUserBlobCrypto _crypto;

    public EncryptedActionConfigStore(EmailAgentDbContext db, IUserBlobCrypto crypto)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
    }

    public async Task<Guid> WriteAsync(Guid ownerUserId, byte[] dek, JsonElement config, CancellationToken ct)
    {
        var (ciphertext, nonce) = _crypto.Encrypt(ownerUserId, config, dek, ct);
        var blob = new EncryptedBlob
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Ciphertext = ciphertext,
            Nonce = nonce,
            Purpose = Purpose,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.EncryptedBlobs.Add(blob);
        await _db.SaveChangesAsync(ct);
        return blob.Id;
    }

    public async Task<Guid> ReplaceAsync(
        Guid ownerUserId, byte[] dek, Guid currentBlobId, JsonElement config, CancellationToken ct)
    {
        var newId = await WriteAsync(ownerUserId, dek, config, ct).ConfigureAwait(false);
        await DeleteAsync(currentBlobId, ct).ConfigureAwait(false);
        return newId;
    }

    public async Task<JsonElement> ReadAsync(Guid ownerUserId, byte[] dek, Guid blobId, CancellationToken ct)
    {
        var blob = await _db.EncryptedBlobs
            .FirstOrDefaultAsync(b => b.Id == blobId, ct)
            .ConfigureAwait(false);

        if (blob is null)
        {
            throw new ActionBlobMissingException(blobId, "no row with that id exists");
        }

        if (blob.OwnerUserId != ownerUserId)
        {
            throw new ActionBlobMissingException(blobId, "the row belongs to a different account");
        }

        if (!string.Equals(blob.Purpose, Purpose, StringComparison.Ordinal))
        {
            throw new ActionBlobMissingException(blobId, $"purpose is '{blob.Purpose}', not '{Purpose}'");
        }

        try
        {
            return _crypto.Decrypt(ownerUserId, blob.Ciphertext, blob.Nonce, dek, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ActionDekInvalidException(blobId, ex);
        }
    }

    public async Task DeleteAsync(Guid blobId, CancellationToken ct)
    {
        var blob = await _db.EncryptedBlobs
            .FirstOrDefaultAsync(b => b.Id == blobId, ct)
            .ConfigureAwait(false);
        if (blob is not null)
        {
            _db.EncryptedBlobs.Remove(blob);
            await _db.SaveChangesAsync(ct);
        }
    }
}
