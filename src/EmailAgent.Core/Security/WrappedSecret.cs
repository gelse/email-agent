namespace EmailAgent.Core.Security;

/// <summary>
/// Result of <see cref="IEncryptionService.WrapWithSystemKey"/> (system-owned
/// secrets — a model API key, §16 / Q-E7) and of
/// <see cref="IEncryptionService.WrapWithUserDek"/> (M2T5 SQ-8 — a user-DEK
/// encrypted source config or message body): the ciphertext together with the
/// 12-byte AES-GCM nonce, both stored in <c>encrypted_blobs.ciphertext</c> /
/// <c>encrypted_blobs.nonce</c>. No system-owned rows are written in M1.
/// </summary>
public sealed record WrappedSecret(byte[] Ciphertext, byte[] Nonce);
