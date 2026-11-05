namespace EmailAgent.Infrastructure.Entities;

/// <summary>
/// §6 <c>encrypted_blobs</c> row: ciphertext + nonce of a DEK-encrypted
/// payload. <c>owner_user_id</c> is NULL for system-owned blobs (model API
/// keys — §7) and is a *soft* reference on purpose: deleting a user orphans
/// their blobs (the DEK is destroyed; rows are purged later by the
/// orphan-blob sweeper — Q-LP2 / SQ-M7-5 / Q-API47), so no FK is mapped.
/// </summary>
public class EncryptedBlob
{
    public Guid Id { get; set; }

    /// <summary>Whose DEK encrypts this blob; NULL for system-owned blobs. No FK (see remarks).</summary>
    public Guid? OwnerUserId { get; set; }

    public byte[] Ciphertext { get; set; } = null!;

    public byte[] Nonce { get; set; } = null!;

    /// <summary>
    /// <c>message_source_config</c> | <c>action_config</c> |
    /// <c>model_api_key</c> | <c>agent_prompt</c> | <c>message_body</c> |
    /// <c>data_export</c>.
    /// </summary>
    public string Purpose { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? RotatedAt { get; set; }
}