using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmailAgent.Core.Security;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace EmailAgent.Api.Sources;

/// <summary>
/// M2T5 (§7 / §6): the single writer/reader for source-owned
/// <c>encrypted_blobs</c> rows — the connector config under
/// <c>purpose='message_source_config'</c> and the fetched body under
/// <c>purpose='message_body'</c> (Q-LP1 / SQ-8).
/// </summary>
/// <remarks>
/// <para>
/// The key is the caller-supplied 32-byte DEK (task Risks SQ-M2-1): the M2
/// <c>X-User-Dek</c> transport hands it over per request, and
/// <see cref="IEncryptionService.WrapWithUserDek"/> binds every blob to
/// <c>owner_user_id</c> through the GCM AAD, so a row can only be opened by
/// the account that wrote it.
/// </para>
/// <para>
/// Every method joins the ambient transaction of the caller (one scoped
/// <c>EmailAgentDbContext</c>), which is what makes the SQ-7 cascade — source
/// row and config blob committed or rolled back together — hold.
/// </para>
/// </remarks>
public sealed class EncryptedSourceConfigStore(EmailAgentDbContext db, IEncryptionService encryption)
{
    /// <summary><c>encrypted_blobs.purpose</c> of a connector configuration.</summary>
    public const string SourceConfigPurpose = "message_source_config";

    /// <summary><c>encrypted_blobs.purpose</c> of a fetched message body (Q-LP1).</summary>
    public const string MessageBodyPurpose = "message_body";

    /// <summary>
    /// Encrypts <paramref name="config"/> under <paramref name="dek"/> and
    /// stores it as a fresh <c>message_source_config</c> row; returns the new
    /// blob id (the value <c>message_sources.config_blob_id</c> takes).
    /// </summary>
    public async Task<Guid> WriteAsync(
        Guid userId, byte[] dek, JsonElement config, CancellationToken ct)
    {
        var plaintext = Encoding.UTF8.GetBytes(config.GetRawText());
        try
        {
            var wrapped = encryption.WrapWithUserDek(userId, dek, plaintext, ct);
            return await AddBlobAsync(
                userId, SourceConfigPurpose, wrapped.Ciphertext, wrapped.Nonce, ct);
        }
        finally
        {
            // The document carries the connector password — clear the copy.
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Encrypts a fetched message body under the same DEK as a
    /// <c>message_body</c> row; the id becomes
    /// <c>processed_messages.body_blob_id</c> (Q-LP1).
    /// </summary>
    public async Task<Guid> WriteBodyAsync(
        Guid userId, byte[] dek, byte[] body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        var wrapped = encryption.WrapWithUserDek(userId, dek, body, ct);
        return await AddBlobAsync(userId, MessageBodyPurpose, wrapped.Ciphertext, wrapped.Nonce, ct);
    }

    /// <summary>
    /// Decrypts the stored blob and returns its configuration document.
    /// Ownership and purpose are enforced server-side; an unauthenticating
    /// unwrap (wrong DEK, foreign row) becomes <see cref="DekInvalidException"/>,
    /// a vanished row <see cref="InternalBlobMissingException"/>.
    /// </summary>
    public async Task<JsonElement> ReadAsync(
        Guid userId, byte[] dek, Guid blobId, CancellationToken ct)
    {
        var blob = await db.EncryptedBlobs.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == blobId, ct)
            ?? throw new InternalBlobMissingException(blobId, "the row does not exist");

        if (blob.OwnerUserId != userId)
        {
            throw new InternalBlobMissingException(blobId, "it is not owned by this account");
        }

        if (!string.Equals(blob.Purpose, SourceConfigPurpose, StringComparison.Ordinal))
        {
            throw new InternalBlobMissingException(
                blobId, $"its purpose is '{blob.Purpose}', not '{SourceConfigPurpose}'");
        }

        byte[] plaintext;
        try
        {
            plaintext = encryption.UnwrapWithUserDek(
                userId, dek, blob.Ciphertext, blob.Nonce, ct);
        }
        catch (CryptographicException ex)
        {
            throw new DekInvalidException(blobId, ex);
        }

        try
        {
            using var document = JsonDocument.Parse(plaintext);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InternalBlobMissingException(blobId, $"its plaintext is not JSON ({ex.Message})");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Deletes one blob row (SQ-7's source-driven cascade, and the backstop
    /// that removes a body blob whose <c>ON CONFLICT DO NOTHING</c> insert
    /// lost a race). Runs in the caller's transaction.
    /// </summary>
    public Task DeleteAsync(Guid blobId, CancellationToken ct)
        => db.EncryptedBlobs
            .Where(b => b.Id == blobId)
            .ExecuteDeleteAsync(ct);

    private async Task<Guid> AddBlobAsync(
        Guid userId,
        string purpose,
        byte[] ciphertext,
        byte[] nonce,
        CancellationToken ct)
    {
        var blob = new EncryptedBlob
        {
            Id = Guid.NewGuid(),
            OwnerUserId = userId,
            Ciphertext = ciphertext,
            Nonce = nonce,
            Purpose = purpose,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.EncryptedBlobs.Add(blob);
        await db.SaveChangesAsync(ct);
        return blob.Id;
    }
}
