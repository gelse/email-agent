using System.Text.Json.Serialization;

namespace EmailAgent.Core.Security;

/// <summary>
/// Per-user Argon2id cost parameters (M1T3 / Q-E2), persisted verbatim as
/// <c>jsonb</c> in <c>users.kdf_params</c> and returned to callers so M1T6
/// can re-derive the KEK on subsequent logins. The record is immutable
/// (init-only) and carries explicit <c>[JsonPropertyName]</c> attributes so
/// <c>System.Text.Json</c> round-trips it independently of any naming
/// policy (step 1 of the M1T3 task).
/// </summary>
public sealed record KdfParams
{
    /// <summary>Argon2id memory cost in KiB (Q-E2: 64 MiB = 65536 KiB).</summary>
    [JsonPropertyName("memory_kb")]
    public int MemoryKb { get; init; }

    /// <summary>Argon2id time cost / passes (Q-E2: t = 3).</summary>
    [JsonPropertyName("iterations")]
    public int Iterations { get; init; }

    /// <summary>Argon2id lanes/threads (Q-E2: p = 4).</summary>
    [JsonPropertyName("parallelism")]
    public int Parallelism { get; init; }

    /// <summary>Derived KEK length in bytes (Q-E2: 32 = 256-bit).</summary>
    [JsonPropertyName("hash_length")]
    public int HashLength { get; init; }

    /// <summary>The Q-E2 resolution defaults: 64 MiB, t = 3, p = 4, 32 bytes.</summary>
    public static KdfParams Default() => new()
    {
        MemoryKb = 65536,
        Iterations = 3,
        Parallelism = 4,
        HashLength = 32,
    };
}