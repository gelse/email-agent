namespace EmailAgent.Core.Security;

/// <summary>
/// Result of <see cref="IEncryptionService.WrapWithSystemKey"/>: a secret
/// (e.g. a model API key) wrapped under the per-install system KEK
/// (<c>SYSTEM__KeyEncryptionKey</c>, §16 / Q-E7) together with the 12-byte
/// AES-GCM nonce. Stored in <c>encrypted_blobs.nonce</c> for system-owned
/// secrets; no such rows are written in M1.
/// </summary>
public sealed record WrappedSecret(byte[] Ciphertext, byte[] Nonce);