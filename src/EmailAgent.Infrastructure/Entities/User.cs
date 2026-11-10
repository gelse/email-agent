using System.Text.Json;
using EmailAgent.Infrastructure.Enums;
using Microsoft.AspNetCore.Identity;

namespace EmailAgent.Infrastructure.Entities;

/// <summary>
/// §6 <c>users</c> row. M1T4 (task step 1): now implements
/// <see cref="IdentityUser{TKey}"/> so ASP.NET Core Identity manages
/// <c>password_hash</c> and the login keys on the existing table. Identity's
/// housekeeping columns are additive to §6 (nothing is removed or renamed)
/// and are created by migration <c>00000000000001_IdentitySchema</c>
/// (Resolution Q-A11 in <c>questions/open-questions-auth.md</c>).
/// Key-material columns (<c>kdf_salt</c>, <c>kdf_params</c>, <c>dek_wrapped</c>,
/// <c>dek_nonce</c>) are all NULL until the user completes first-login
/// passphrase set (Q-E6 / SQ-11 / Q-E8) — the invariant is enforced in the
/// application layer (M1T6), deliberately *not* by a DB CHECK (M1T2 task step 4).
/// </summary>
public class User : IdentityUser<Guid>
{
    public UserRole Role { get; set; }

    public UserStatus Status { get; set; }

    /// <summary>§6 default <c>true</c> — forced first-login change (Q-A4).</summary>
    public bool MustChangePassword { get; set; } = true;

    /// <summary>Salt for Argon2id KEK derivation; NULL until set-passphrase (SQ-11).</summary>
    public byte[]? KdfSalt { get; set; }

    /// <summary>Argon2id memory/t/p parameters as jsonb; NULL until set-passphrase (SQ-11).</summary>
    public JsonDocument? KdfParams { get; set; }

    /// <summary>DEK wrapped with the KEK (AES-GCM ciphertext+tag); NULL until set-passphrase (Q-E6).</summary>
    public byte[]? DekWrapped { get; set; }

    /// <summary>AES-GCM nonce accompanying <see cref="DekWrapped"/>.</summary>
    public byte[]? DekNonce { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }

    public ICollection<OneTimePassword> OneTimePasswords { get; set; } = [];

    public ICollection<ModelCatalogEntry> CreatedModelCatalogEntries { get; set; } = [];

    public ICollection<MessageSource> MessageSources { get; set; } = [];

    public ICollection<AgentConfig> AgentConfigs { get; set; } = [];

    public ICollection<ActionConfig> ActionConfigs { get; set; } = [];

    public ICollection<UserNotification> UserNotifications { get; set; } = [];

    public ICollection<AuditLog> AuditLogEntries { get; set; } = [];

    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];
}
