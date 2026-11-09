using System.Text.Json;
using EmailAgent.Core.Security;

namespace EmailAgent.Infrastructure.Blobs;

/// <summary>
/// M4T1: thin JSON facade over <see cref="IEncryptionService"/>'s per-user
/// DEK wrap/unwrap. Both the dispatcher-side reader
/// (<c>DecryptedActionConfigProvider</c>) and the API-side action-config
/// writer share this type so writer and reader cannot drift on byte format.
/// The AAD owner binding lives in <see cref="IEncryptionService"/> (SQ-8);
/// <paramref name="userId"/> is passed through on every call.
/// </summary>
public interface IUserBlobCrypto
{
    /// <summary>Decrypts a blob and returns the parsed JSON root (cloned).</summary>
    JsonElement Decrypt(
        Guid userId, byte[] ciphertext, byte[] nonce, ReadOnlyMemory<byte> userDek,
        CancellationToken ct = default);

    /// <summary>Serializes <paramref name="value"/> and wraps it with the user's DEK.</summary>
    (byte[] Ciphertext, byte[] Nonce) Encrypt(
        Guid userId, JsonElement value, ReadOnlyMemory<byte> userDek,
        CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class UserBlobCrypto : IUserBlobCrypto
{
    private readonly IEncryptionService _encryption;

    public UserBlobCrypto(IEncryptionService encryption) => _encryption = encryption;

    /// <inheritdoc />
    public JsonElement Decrypt(
        Guid userId, byte[] ciphertext, byte[] nonce, ReadOnlyMemory<byte> userDek,
        CancellationToken ct = default)
    {
        var dek = userDek.ToArray();
        try
        {
            var plaintext = _encryption.UnwrapWithUserDek(userId, dek, ciphertext, nonce, ct);
            try
            {
                using var doc = JsonDocument.Parse(plaintext);
                return doc.RootElement.Clone();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <inheritdoc />
    public (byte[] Ciphertext, byte[] Nonce) Encrypt(
        Guid userId, JsonElement value, ReadOnlyMemory<byte> userDek,
        CancellationToken ct = default)
    {
        var dek = userDek.ToArray();
        try
        {
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(value);
            try
            {
                var wrapped = _encryption.WrapWithUserDek(userId, dek, plaintext, ct);
                return (wrapped.Ciphertext, wrapped.Nonce);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }
}

file static class CryptographicOperations
{
    public static void ZeroMemory(byte[] data) => System.Security.Cryptography.CryptographicOperations.ZeroMemory(data);
}
