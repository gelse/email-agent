using System.Security.Cryptography;
using System.Text;
using EmailAgent.Api.Models;
using EmailAgent.Core.Security;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Security;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EmailAgent.UnitTests.Models;

// ---------------------------------------------------------------------------
// M3T3: the system-keyed model API-key store (task-named unit tests). EF
// InMemory + the real Argon2id/AES-GCM service bound to a known throwaway
// SYSTEM__KeyEncryptionKey — no HTTP, no container, no real secret (AGENTS.md
// test constraint).
// ---------------------------------------------------------------------------

[Trait("Category", "M3T3")]
public class EncryptedModelApiKeyStoreTests
{
    /// <summary>Throwaway ≥ 32-byte test key (never a real secret).</summary>
    private const string SystemKey = "m3t3-unit-system-kek-0123456789abcdef";

    /// <summary>A different throwaway ≥ 32-byte key — must NOT open our blobs.</summary>
    private const string OtherSystemKey = "m3t3-unit-other-kek-fedcba9876543210";

    private const string ApiKey = "sk-m3t3-unit-roundtrip";

    // -----------------------------------------------------------------------
    // Task-named test: round-trip through Wrap/Unwrap with a known system key.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task EncryptedModelApiKeyStore_RoundTrip_WithKnownSystemKey()
    {
        await using var db = CreateDb();
        var store = new EncryptedModelApiKeyStore(db, CreateEncryption(), TimeProvider.System);

        var blobId = await store.WriteAsync(ApiKey, CancellationToken.None);

        var blob = await db.EncryptedBlobs.AsNoTracking().SingleAsync(b => b.Id == blobId);
        blob.Purpose.Should().Be(
            EncryptedModelApiKeyStore.ModelApiKeyPurpose, "Q-E7: model keys live under purpose=model_api_key");
        blob.OwnerUserId.Should().BeNull("Q-E7: the catalog's key is system-owned, never a user DEK");
        blob.CreatedAt.Should().NotBe(default(DateTimeOffset), "the insert stamps created_at");
        blob.RotatedAt.Should().NotBeNull("the insert stamps rotated_at");
        blob.Nonce.Length.Should().BeGreaterThan(0);

        // Acceptance criterion: the ciphertext is not the raw key bytes.
        ContainsBytes(blob.Ciphertext, Encoding.UTF8.GetBytes(ApiKey)).Should().BeFalse(
            "the key must be wrapped under the system key, never stored in clear");

        var roundTrip = await store.ReadAsync(blobId, CancellationToken.None);
        roundTrip.Should().Be(ApiKey, "Wrap/Unwrap with the same known system key must round-trip");

        // Q-E7 binding: ciphertext + nonce are only readable with THIS install's
        // system key — a different key must fail the GCM tag check.
        var otherKey = CreateEncryption(OtherSystemKey);
        Action unwrapWithWrongKey = () => otherKey.UnwrapWithSystemKey(blob.Ciphertext, blob.Nonce);
        unwrapWithWrongKey.Should().Throw<CryptographicException>(
            "the blob is bound to SYSTEM__KeyEncryptionKey (Q-E7)");
    }

    // -----------------------------------------------------------------------
    // Typed integrity failures on read.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task EncryptedModelApiKeyStore_ReadAsync_ThrowsOnInvalidPurpose()
    {
        await using var db = CreateDb();
        var store = new EncryptedModelApiKeyStore(db, CreateEncryption(), TimeProvider.System);

        var blobId = Guid.NewGuid();
        db.EncryptedBlobs.Add(
            new EncryptedBlob
            {
                Id = blobId,
                OwnerUserId = Guid.NewGuid(),
                Ciphertext = new byte[] { 1, 2, 3, 4 },
                Nonce = new byte[12],
                Purpose = "message_source_config",
                CreatedAt = DateTimeOffset.UtcNow,
                RotatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();

        var act = () => store.ReadAsync(blobId, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<InvalidBlobPurposeException>();
        exception.Which.Purpose.Should().Be(
            "message_source_config", "a foreign purpose is a server-side integrity failure, never a client error");
    }

    [Fact]
    public async Task EncryptedModelApiKeyStore_ReadAsync_ThrowsOnMissingBlob()
    {
        await using var db = CreateDb();
        var store = new EncryptedModelApiKeyStore(db, CreateEncryption(), TimeProvider.System);

        var missing = Guid.NewGuid();

        var act = () => store.ReadAsync(missing, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<EncryptedBlobNotFoundException>();
        exception.Which.BlobId.Should().Be(missing);
    }

    // -----------------------------------------------------------------------
    // Stubs.
    // -----------------------------------------------------------------------

    /// <summary>
    /// The InMemory seam the probe suite established: EF InMemory cannot map
    /// the Npgsql-native jsonb column behind <see cref="User.KdfParams"/> (the
    /// only JsonDocument-typed entity property), so this test context drops
    /// exactly that one property. The production mapping is untouched.
    /// </summary>
    private sealed class InMemoryStoreDbContext(DbContextOptions<EmailAgentDbContext> options)
        : EmailAgentDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<User>().Ignore(u => u.KdfParams);
        }
    }

    private static InMemoryStoreDbContext CreateDb() => new(
        new DbContextOptionsBuilder<EmailAgentDbContext>()
            .UseInMemoryDatabase($"m3t3-store-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    /// <summary>The real M1T3 service bound to a known test key (M3T3 round-trip).</summary>
    private static IEncryptionService CreateEncryption(string key = SystemKey)
        => new Argon2idAesGcmEncryptionService(
            Options.Create(new EncryptionOptions { KeyEncryptionKey = key }),
            NullLogger<Argon2idAesGcmEncryptionService>.Instance);

    /// <summary>Little-endian-free subsequence search (never culture- or encoding-sensitive).</summary>
    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        for (var offset = 0; offset + needle.Length <= haystack.Length; offset++)
        {
            if (haystack.AsSpan(offset, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }
}
