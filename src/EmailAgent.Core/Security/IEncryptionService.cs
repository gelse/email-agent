namespace EmailAgent.Core.Security;

/// <summary>
/// M1T3 encryption surface (architecture §7): Argon2id KEK derivation,
/// AES-256-GCM DEK wrap/unwrap, the per-install system-key helpers for
/// system-owned secrets (model API keys, Q-E7) and — added by M2T5 for
/// source configs and message bodies (SQ-8) — the per-user DEK wrap/unwrap
/// pair. Pure library contract — no HTTP and no EF Core dependency — so it
/// unit-tests without infrastructure; M1T6 wires it to the account
/// endpoints, M2T5 to <c>/v1/user/sources</c>, and M5 stores the outputs.
/// </summary>
public interface IEncryptionService
{
    /// <summary>The Q-E2 Argon2id defaults (64 MiB, t = 3, p = 4, 256-bit KEK).</summary>
    KdfParams GetDefaultKdfParams();

    /// <summary>
    /// Derives the per-user key-encryption key with Argon2id
    /// (<paramref name="userId"/> keys the per-user zero-wait mutex — throws
    /// <see cref="KekDerivationBusyException"/> when another derivation for
    /// the same user is in flight, Q-E5).
    /// </summary>
    byte[] DeriveKek(Guid userId, string passphrase, byte[] salt, KdfParams parameters);

    /// <summary>
    /// Mints a random 32-byte DEK and wraps it under the KEK derived from
    /// <paramref name="passphrase"/>; returns the wrapped bytes, nonce and
    /// the fresh plaintext DEK for immediate use by the caller (Q-E6).
    /// </summary>
    WrappedDek GenerateAndWrapDek(Guid userId, string passphrase, byte[] salt, KdfParams parameters);

    /// <summary>
    /// Derives the KEK and unwraps <paramref name="dekWrapped"/>. Throws
    /// <see cref="InvalidPassphraseException"/> when the GCM tag does not
    /// authenticate (wrong passphrase, nonce or ciphertext) and
    /// <see cref="KekDerivationBusyException"/> when the per-user mutex is
    /// held (Q-E5; HTTP 429 mapping happens in M1T6).
    /// </summary>
    byte[] UnwrapDek(
        Guid userId,
        string passphrase,
        byte[] salt,
        KdfParams parameters,
        byte[] dekWrapped,
        byte[] dekNonce);

    /// <summary>
    /// Wraps a system-owned secret (e.g. a model API key) under the
    /// per-install system KEK loaded once from <c>SYSTEM__KeyEncryptionKey</c>
    /// (Q-E7). No system blobs are written in M1 — exercised by unit tests.
    /// </summary>
    WrappedSecret WrapWithSystemKey(byte[] plaintext);

    /// <summary>Unwraps a blob produced by <see cref="WrapWithSystemKey"/>.</summary>
    byte[] UnwrapWithSystemKey(byte[] ciphertext, byte[] nonce);

    /// <summary>
    /// M2T5 (SQ-8): wraps <paramref name="plaintext"/> under the caller-supplied
    /// 32-byte per-user data-encryption key with AES-256-GCM and a fresh
    /// 12-byte nonce. The GCM additional-authenticated-data binds the blob to
    /// <paramref name="userId"/>, so ciphertext written for one account can
    /// never be opened with another account's DEK even if a row were
    /// re-pointed. The DEK itself is never derived here — the caller supplies
    /// it (the M2 <c>X-User-Dek</c> transport, §13.16) because only the
    /// passphrase holder can derive the KEK.
    /// </summary>
    /// <returns>Ciphertext || tag plus the nonce — both stored in an <c>encrypted_blobs</c> row.</returns>
    WrappedSecret WrapWithUserDek(
        Guid userId, byte[] dek, byte[] plaintext, CancellationToken ct = default);

    /// <summary>
    /// Unwraps a blob produced by <see cref="WrapWithUserDek"/>. Throws
    /// <see cref="CryptographicException"/> when the tag does not authenticate
    /// (wrong DEK, nonce, owner binding or tampered ciphertext) — the caller
    /// maps that to a §13.12 problem document, never to a 500.
    /// </summary>
    byte[] UnwrapWithUserDek(
        Guid userId, byte[] dek, byte[] ciphertext, byte[] nonce, CancellationToken ct = default);
}
