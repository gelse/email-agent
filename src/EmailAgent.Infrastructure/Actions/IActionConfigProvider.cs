using System.Text.Json;

namespace EmailAgent.Infrastructure.Actions;

/// <summary>M4T1: resolved, decrypted action configuration for a user + type.</summary>
public sealed record ActionConfigEnvelope(
    Guid Id,
    string TypeId,
    string DisplayName,
    bool RequireConfirmation,
    bool DryRunDefault,
    JsonElement Config);

/// <summary>
/// M4T1: resolves and decrypts the per-user action config the dispatcher (and
/// tests) consume. Unit-testable via the interface; production reads through
/// <see cref="DecryptedActionConfigProvider"/>.
/// </summary>
public interface IActionConfigProvider
{
    Task<ActionConfigEnvelope?> ResolveAsync(
        Guid userId, string typeId, ReadOnlyMemory<byte> userDek, CancellationToken ct);
}
