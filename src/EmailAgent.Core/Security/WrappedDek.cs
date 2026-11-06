namespace EmailAgent.Core.Security;

/// <summary>
/// Result of <see cref="IEncryptionService.GenerateAndWrapDek"/>: the
/// AES-GCM-wrapped data-encryption key, its nonce, and the freshly minted
/// plaintext DEK. The plaintext <c>Dek</c> is only present here (never on
/// <see cref="IEncryptionService.UnwrapDek"/>'s output path beyond the
/// returned bytes) because the caller uses it immediately to wrap
/// <c>encrypted_blobs</c> rows at first-login passphrase set (Q-E6).
/// Sizes (fixed by M1T3 acceptance criteria): <c>Wrapped</c> is exactly
/// 48 bytes (32-byte ciphertext + 16-byte GCM tag), <c>Nonce</c> is exactly
/// 12 bytes, <c>Dek</c> is exactly 32 bytes.
/// </summary>
public sealed record WrappedDek(byte[] Wrapped, byte[] Nonce, byte[] Dek);
