using System.Security.Cryptography;
using System.Text;
using EmailAgent.Core.Security;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace EmailAgent.Api.Models;

/// <summary>
/// M3T3 (§7 / Q-E7): the single writer/reader for catalog-owned
/// <c>encrypted_blobs</c> rows under <c>purpose='model_api_key'</c> with
/// <c>owner_user_id = NULL</c> — system-owned secrets wrapped with the
/// per-install <c>SYSTEM__KeyEncryptionKey</c>, never a user DEK.
/// </summary>
/// <remarks>
/// <para>
/// Every method runs on the request-scoped <see cref="EmailAgentDbContext"/>,
/// so a write or delete joins the caller's ambient transaction — which is what
/// makes the M3T3 atomicity rules hold: catalog row + key blob commit together
/// on create/PATCH, and catalog row + key blob disappear together on DELETE.
/// </para>
/// <para>
/// The plaintext key only ever lives in a short-lived local buffer that is
/// zeroed in a <c>finally</c> (parity with
/// <see cref="EmailAgent.Api.Sources.EncryptedSourceConfigStore"/>); it is
/// never returned by any endpoint, never logged and never audited.
/// </para>
/// </remarks>
public sealed class EncryptedModelApiKeyStore
{
    /// <summary><c>encrypted_blobs.purpose</c> of a model API key (Q-E7).</summary>
    public const string ModelApiKeyPurpose = "model_api_key";

    private readonly EmailAgentDbContext _db;
    private readonly IEncryptionService _encryption;
    private readonly TimeProvider _clock;

    public EncryptedModelApiKeyStore(
        EmailAgentDbContext db, IEncryptionService encryption, TimeProvider clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _encryption = encryption ?? throw new ArgumentNullException(nameof(encryption));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Wraps <paramref name="apiKey"/> under the system key and stores a fresh
    /// <see cref="ModelApiKeyPurpose"/> row (system-owned: <c>owner_user_id</c>
    /// NULL); returns the blob id the <c>model_catalog.api_key_secret_ref</c>
    /// column takes. The insert is saved inside the caller's transaction.
    /// </summary>
    /// <remarks>
    /// DEVIATION (M3T3): the task snippet's <c>createdByUserId</c> parameter is
    /// dropped — the column it would feed does not exist on
    /// <see cref="EncryptedBlob"/> and the creating admin is recorded by
    /// <c>model_catalog.created_by</c> plus the <c>create_model</c> audit row.
    /// </remarks>
    public async Task<Guid> WriteAsync(string apiKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(apiKey);

        var plaintext = Encoding.UTF8.GetBytes(apiKey);
        try
        {
            var wrapped = _encryption.WrapWithSystemKey(plaintext);
            var blob = new EncryptedBlob
            {
                Id = Guid.NewGuid(),
                OwnerUserId = null, // system-owned per Q-E7
                Ciphertext = wrapped.Ciphertext,
                Nonce = wrapped.Nonce,
                Purpose = ModelApiKeyPurpose,
                CreatedAt = _clock.GetUtcNow(),
                RotatedAt = _clock.GetUtcNow(),
            };

            _db.EncryptedBlobs.Add(blob);
            await _db.SaveChangesAsync(ct);
            return blob.Id;
        }
        finally
        {
            // The buffer carries the credential — clear the copy.
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Decrypts the stored blob and returns the plaintext key (needed when a
    /// <c>PATCH</c> omits <c>api_key</c>: the effective engine config must
    /// carry the stored key to be schema-validated and probed). A vanished row
    /// is <see cref="EncryptedBlobNotFoundException"/>, a foreign purpose
    /// <see cref="InvalidBlobPurposeException"/> — both are §13.12 500s
    /// (server-side integrity, never a client error).
    /// </summary>
    public async Task<string> ReadAsync(Guid blobId, CancellationToken ct)
    {
        var blob = await _db.EncryptedBlobs.AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == blobId, ct)
            ?? throw new EncryptedBlobNotFoundException(blobId);

        if (!string.Equals(blob.Purpose, ModelApiKeyPurpose, StringComparison.Ordinal))
        {
            throw new InvalidBlobPurposeException(blob.Purpose);
        }

        var plaintext = _encryption.UnwrapWithSystemKey(blob.Ciphertext, blob.Nonce);
        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Deletes one key blob row (the create/PATCH swap and the DELETE cascade),
    /// joining the caller's transaction so the catalog row and the blob commit
    /// or roll back together.
    /// </summary>
    public Task DeleteAsync(Guid blobId, CancellationToken ct)
        => _db.EncryptedBlobs
            .Where(b => b.Id == blobId)
            .ExecuteDeleteAsync(ct);
}

/// <summary>
/// The referenced <c>encrypted_blobs</c> row does not exist — a server-side
/// integrity violation (an <c>api_key_secret_ref</c> pointing at nothing), so
/// the endpoint layer answers §13.12 500 <c>/errors/internal-blob-missing</c>.
/// </summary>
public sealed class EncryptedBlobNotFoundException : Exception
{
    public EncryptedBlobNotFoundException(Guid blobId)
        : base($"Encrypted blob {blobId} does not exist.") => BlobId = blobId;

    public Guid BlobId { get; }
}

/// <summary>
/// The referenced blob carries a purpose other than
/// <see cref="EncryptedModelApiKeyStore.ModelApiKeyPurpose"/> — same §13.12
/// 500 <c>/errors/internal-blob-missing</c> class as
/// <see cref="EncryptedBlobNotFoundException"/> (never a client error).
/// </summary>
public sealed class InvalidBlobPurposeException : Exception
{
    public InvalidBlobPurposeException(string purpose)
        : base($"Encrypted blob purpose is '{purpose}', not "
            + $"'{EncryptedModelApiKeyStore.ModelApiKeyPurpose}'.")
        => Purpose = purpose;

    public string Purpose { get; }
}
