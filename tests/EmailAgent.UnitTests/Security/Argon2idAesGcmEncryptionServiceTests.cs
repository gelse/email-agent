using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmailAgent.Core.Security;
using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EmailAgent.UnitTests.Security;

/// <summary>
/// M1T3 unit tests for <see cref="Argon2idAesGcmEncryptionService"/>: KEK
/// derivation, DEK wrap/unwrap round-trips and failure modes, KDF parameter
/// round-trip, system-key helpers, the per-user KEK-derivation mutex (Q-E5),
/// startup fail-fast for <c>SYSTEM__KeyEncryptionKey</c> (Q-E7), and the
/// no-secrets-in-logs rule. Pure library — no external services.
/// </summary>
public class Argon2idAesGcmEncryptionServiceTests
{
    /// <summary>Ephemeral test passphrase — never a real credential.</summary>
    private const string Passphrase = "correct-horse-battery-staple-42!";

    /// <summary>Ephemeral 37-byte test system key (>= 32-byte floor).</summary>
    private const string SystemKey = "unit-test-system-kek-0123456789abcdef";

    private const string SystemPlaintext = "SENTINEL-SYSTEM-PLAINTEXT-9876";

    private static readonly Guid UserId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static byte[] Salt(byte fill = 0x5A)
        => Enumerable.Repeat(fill, 16).ToArray();

    private static Argon2idAesGcmEncryptionService CreateService(
        ILogger<Argon2idAesGcmEncryptionService>? logger = null,
        string? systemKey = SystemKey)
        => new(
            Options.Create(new EncryptionOptions { KeyEncryptionKey = systemKey }),
            logger ?? NullLogger<Argon2idAesGcmEncryptionService>.Instance);

    // ---------------------------------------------------------------- KEK

    [Fact]
    public void DeriveKek_SamePassphraseSameSalt_ProducesSameKey()
    {
        var service = CreateService();
        var salt = Salt();

        var first = service.DeriveKek(UserId, Passphrase, salt, KdfParams.Default());
        var second = service.DeriveKek(UserId, Passphrase, salt, KdfParams.Default());

        first.Should().HaveCount(32, "the KEK is 256 bits");
        second.Should().Equal(first, "Argon2id is deterministic for the same passphrase + salt + params");
    }

    [Fact]
    public void DeriveKek_DifferentSalt_ProducesDifferentKey()
    {
        var service = CreateService();

        var first = service.DeriveKek(UserId, Passphrase, Salt(0x01), KdfParams.Default());
        var second = service.DeriveKek(UserId, Passphrase, Salt(0x02), KdfParams.Default());

        second.Should().NotEqual(first, "a different per-user salt must yield a different KEK");
    }

    // ----------------------------------------------------------- DEK wrap

    [Fact]
    public void GenerateAndWrapDek_DekIsRandom32Bytes()
    {
        var service = CreateService();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < 10; i++)
        {
            var wrapped = service.GenerateAndWrapDek(UserId, Passphrase, Salt(), KdfParams.Default());

            wrapped.Dek.Should().HaveCount(32, "the DEK is 256 bits");
            wrapped.Wrapped.Should().HaveCount(48, "wrapped = 32-byte ciphertext + 16-byte GCM tag");
            wrapped.Nonce.Should().HaveCount(12, "AES-GCM nonce size");
            seen.Add(Convert.ToHexString(wrapped.Dek))
                .Should().BeTrue("every generated DEK must be distinct");
        }
    }

    [Fact]
    public void GenerateAndWrapDek_Then_UnwrapDek_RoundTrips_PlaintextDek()
    {
        var service = CreateService();
        var salt = Salt();

        var wrapped = service.GenerateAndWrapDek(UserId, Passphrase, salt, KdfParams.Default());

        // Exactly the bytes a caller would persist as dek_wrapped / dek_nonce.
        var unwrapped = service.UnwrapDek(
            UserId, Passphrase, salt, KdfParams.Default(), wrapped.Wrapped, wrapped.Nonce);

        unwrapped.Should().Equal(wrapped.Dek, "unwrap must reproduce the minted DEK byte-for-byte");
    }

    [Fact]
    public void UnwrapDek_WrongPassphrase_ThrowsInvalidPassphraseException()
        => AssertWrongPassphraseFails();

    /// <summary>Exit criterion 2 verification entry (milestone test name).</summary>
    [Fact]
    public void EncryptionService_TryUnwrapDek_WrongPassphrase_Fails()
        => AssertWrongPassphraseFails();

    private static void AssertWrongPassphraseFails()
    {
        var service = CreateService();
        var salt = Salt();
        var wrapped = service.GenerateAndWrapDek(UserId, Passphrase, salt, KdfParams.Default());

        var act = () => service.UnwrapDek(
            UserId,
            "definitely-not-the-passphrase",
            salt,
            KdfParams.Default(),
            wrapped.Wrapped,
            wrapped.Nonce);

        act.Should().Throw<InvalidPassphraseException>(
            "a GCM tag mismatch under the wrong KEK is a wrong passphrase");
    }

    [Fact]
    public void UnwrapDek_WrongNonce_ThrowsInvalidPassphraseException()
    {
        var service = CreateService();
        var salt = Salt();
        var wrapped = service.GenerateAndWrapDek(UserId, Passphrase, salt, KdfParams.Default());
        var wrongNonce = RandomNumberGenerator.GetBytes(12);

        var act = () => service.UnwrapDek(
            UserId, Passphrase, salt, KdfParams.Default(), wrapped.Wrapped, wrongNonce);

        act.Should().Throw<InvalidPassphraseException>();
    }

    [Fact]
    public void UnwrapDek_TamperedCiphertext_ThrowsInvalidPassphraseException()
    {
        var service = CreateService();
        var salt = Salt();
        var wrapped = service.GenerateAndWrapDek(UserId, Passphrase, salt, KdfParams.Default());
        var tampered = (byte[])wrapped.Wrapped.Clone();
        tampered[0] ^= 0xFF;

        var act = () => service.UnwrapDek(
            UserId, Passphrase, salt, KdfParams.Default(), tampered, wrapped.Nonce);

        act.Should().Throw<InvalidPassphraseException>();
    }

    [Fact]
    public void UnwrapDek_MalformedNonceOrWrapped_ThrowsInvalidPassphraseException()
    {
        var service = CreateService();
        var salt = Salt();

        // Shape checks run before Argon2id, so no wrap setup is needed: a
        // wrong-length nonce or a blob that cannot hold ciphertext+tag must
        // surface as the typed exception (never an untyped ArgumentException).
        var wrongLengthNonce = () => service.UnwrapDek(
            UserId, Passphrase, salt, KdfParams.Default(), new byte[48], new byte[11]);
        var tooShortWrapped = () => service.UnwrapDek(
            UserId, Passphrase, salt, KdfParams.Default(), new byte[10], new byte[12]);

        wrongLengthNonce.Should().Throw<InvalidPassphraseException>();
        tooShortWrapped.Should().Throw<InvalidPassphraseException>();
    }

    [Fact]
    public void DeriveKek_UnsupportedKdfParamsOrShortSalt_ThrowsArgumentException()
    {
        var service = CreateService();

        // All guards run before Argon2id allocates, so these fail fast and
        // cheap: a corrupt/hostile kdf_params row must not trigger a
        // multi-gigabyte allocation or a non-256-bit KEK.
        var shortSalt = () => service.DeriveKek(UserId, Passphrase, new byte[4], KdfParams.Default());
        var shortHash = () => service.DeriveKek(
            UserId, Passphrase, Salt(), KdfParams.Default() with { HashLength = 16 });
        var hugeMemory = () => service.DeriveKek(
            UserId, Passphrase, Salt(), KdfParams.Default() with { MemoryKb = 1073741824 });
        var zeroIterations = () => service.DeriveKek(
            UserId, Passphrase, Salt(), KdfParams.Default() with { Iterations = 0 });

        shortSalt.Should().Throw<ArgumentException>();
        shortHash.Should().Throw<ArgumentException>();
        hugeMemory.Should().Throw<ArgumentException>();
        zeroIterations.Should().Throw<ArgumentException>();
    }

    // -------------------------------------------------------- System key

    [Fact]
    public void WrapWithSystemKey_Then_UnwrapWithSystemKey_RoundTrips()
    {
        var service = CreateService();
        var plaintext = Encoding.UTF8.GetBytes(SystemPlaintext);

        var wrapped = service.WrapWithSystemKey(plaintext);

        wrapped.Nonce.Should().HaveCount(12);
        wrapped.Ciphertext.Should().HaveCount(plaintext.Length + 16, "ciphertext + GCM tag");
        service.UnwrapWithSystemKey(wrapped.Ciphertext, wrapped.Nonce)
            .Should().Equal(plaintext);

        // A different installation key must not unwrap it.
        var otherService = CreateService(systemKey: "another-install-system-key-9876543210");
        var act = () => otherService.UnwrapWithSystemKey(wrapped.Ciphertext, wrapped.Nonce);
        act.Should().Throw<CryptographicException>();

        // Exactly-32-byte key: used verbatim (the longer-key SHA-256 rule
        // must not kick in at the boundary).
        var exactKeyService = CreateService(
            systemKey: "0123456789abcdef0123456789abcdef");
        var exactWrapped = exactKeyService.WrapWithSystemKey(plaintext);
        exactKeyService.UnwrapWithSystemKey(exactWrapped.Ciphertext, exactWrapped.Nonce)
            .Should().Equal(plaintext);
        var crossAct = () => service.UnwrapWithSystemKey(
            exactWrapped.Ciphertext, exactWrapped.Nonce);
        crossAct.Should().Throw<CryptographicException>(
            "a key of exactly 32 bytes must differ from the longer test key");
    }

    // -------------------------------------------------------- KdfParams

    [Fact]
    public void KdfParams_Default_Matches_Q_E2()
    {
        var defaults = KdfParams.Default();

        defaults.MemoryKb.Should().Be(65536, "Q-E2: 64 MiB memory cost");
        defaults.Iterations.Should().Be(3, "Q-E2: t = 3");
        defaults.Parallelism.Should().Be(4, "Q-E2: p = 4");
        defaults.HashLength.Should().Be(32, "Q-E2: 256-bit KEK");

        CreateService().GetDefaultKdfParams().Should().Be(defaults);
    }

    [Fact]
    public void KdfParams_Serialize_Deserialize_RoundTrips()
    {
        var service = CreateService();
        var original = KdfParams.Default();

        var json = JsonSerializer.Serialize(original);
        json.Should().Contain("memory_kb").And.Contain("65536");

        var roundTripped = JsonSerializer.Deserialize<KdfParams>(json);
        roundTripped.Should().Be(original, "the jsonb payload must round-trip into an equal record");
        roundTripped!.HashLength.Should().Be(original.HashLength);

        // Round-tripped parameters must re-derive the same KEK (acceptance
        // criterion: round-trip through System.Text.Json and re-derive).
        var fromOriginal = service.DeriveKek(UserId, Passphrase, Salt(), original);
        var fromJson = service.DeriveKek(UserId, Passphrase, Salt(), roundTripped);
        fromJson.Should().Equal(fromOriginal);
    }

    // ------------------------------------------------------ KEK mutex

    [Fact]
    public void ConcurrentUnwrapDek_ForSameUser_OnlyOneArgon2idRuns()
    {
        const int callerCount = 10;
        var service = CreateService();
        var salt = Salt();

        // Q-E2 cost at 4x memory for this test only: it widens the mutex
        // critical section (~0.75 s of Argon2id instead of ~0.19 s), so every
        // loser reaches the zero-wait Wait(0) long before the winner
        // releases — the barrier synchronizes the start, the longer run
        // makes the "exactly 1 success / 9 busy" outcome robust on loaded
        // CI hosts. Setup and unwraps must share the parameters, otherwise
        // the winner's KEK would differ and unwrap would fail.
        var parameters = KdfParams.Default() with { MemoryKb = 262144 };
        var setup = service.GenerateAndWrapDek(UserId, Passphrase, salt, parameters);

        // Dedicated threads + barrier: all 10 callers hit the zero-wait mutex
        // simultaneously, so exactly one runs Argon2id and nine must observe
        // KekDerivationBusyException (Q-E5).
        using var barrier = new Barrier(callerCount + 1);
        var errors = new Exception?[callerCount];
        var deks = new byte[callerCount][];
        var threads = new Thread[callerCount];

        for (var i = 0; i < callerCount; i++)
        {
            var index = i;
            threads[i] = new Thread(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    deks[index] = service.UnwrapDek(
                        UserId, Passphrase, salt, parameters, setup.Wrapped, setup.Nonce);
                }
                catch (Exception ex)
                {
                    errors[index] = ex;
                }
            })
            {
                IsBackground = true,
            };
            threads[i].Start();
        }

        barrier.SignalAndWait();
        foreach (var thread in threads)
        {
            thread.Join(TimeSpan.FromSeconds(120))
                .Should().BeTrue("every concurrent caller must finish");
        }

        var successes = errors.Count(e => e is null);
        var busy = errors.Count(e => e is KekDerivationBusyException);
        var unexpected = errors.Count(e => e is not null && e is not KekDerivationBusyException);

        successes.Should().Be(1, "exactly one caller runs Argon2id");
        busy.Should().Be(callerCount - 1, "the other callers observe the typed busy exception");
        unexpected.Should().Be(0);

        var winner = Array.FindIndex(errors, e => e is null);
        deks[winner].Should().Equal(setup.Dek, "the winner must unwrap the correct DEK");
    }

    // ------------------------------------------------- Startup fail-fast

    [Fact]
    public void EncryptionOptions_MissingOrShortSystemKey_FailsFast()
    {
        var cases = new (string? Label, string? Key)[]
        {
            ("missing", null),
            ("empty", string.Empty),
            ("shorter than 32 bytes", new string('k', 31)),
        };

        foreach (var (label, key) in cases)
        {
            var services = new ServiceCollection();
            var values = new Dictionary<string, string?>();
            if (key is not null)
            {
                values["System:KeyEncryptionKey"] = key;
            }

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            services.AddEncryption(configuration);
            using var provider = services.BuildServiceProvider();

            var resolve = () => provider.GetRequiredService<IOptions<EncryptionOptions>>().Value;

            resolve.Should().Throw<OptionsValidationException>(
                $"a {label} SYSTEM__KeyEncryptionKey must be rejected before the host starts");
        }
    }

    [Fact]
    public void Constructor_MissingOrShortSystemKey_ThrowsOptionsValidationException()
    {
        var missing = () => CreateService(systemKey: null);
        var shortKey = () => CreateService(systemKey: new string('k', 31));

        missing.Should().Throw<OptionsValidationException>();
        shortKey.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void AddEncryption_ValidSystemKey_RegistersWorkingService()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["System:KeyEncryptionKey"] = SystemKey,
            })
            .Build();

        services.AddEncryption(configuration);
        services.AddLogging();
        using var provider = services.BuildServiceProvider();

        var service = provider.GetRequiredService<IEncryptionService>();
        var salt = Salt();
        var wrapped = service.GenerateAndWrapDek(UserId, Passphrase, salt, KdfParams.Default());
        service.UnwrapDek(UserId, Passphrase, salt, KdfParams.Default(), wrapped.Wrapped, wrapped.Nonce)
            .Should().Equal(wrapped.Dek);
    }

    // -------------------------------------------------- No secrets in logs

    [Fact]
    public void ServiceOperations_NeverLogSecretMaterial()
    {
        var logger = new RecordingLogger<Argon2idAesGcmEncryptionService>();
        var service = CreateService(logger);
        var salt = Salt();

        service.DeriveKek(UserId, Passphrase, salt, KdfParams.Default());
        var wrapped = service.GenerateAndWrapDek(UserId, Passphrase, salt, KdfParams.Default());
        service.UnwrapDek(UserId, Passphrase, salt, KdfParams.Default(), wrapped.Wrapped, wrapped.Nonce);
        var systemWrapped = service.WrapWithSystemKey(Encoding.UTF8.GetBytes(SystemPlaintext));
        service.UnwrapWithSystemKey(systemWrapped.Ciphertext, systemWrapped.Nonce);

        logger.Entries.Should().NotBeEmpty("the operations must have been observed by the logger");
        var forbidden = new[] { Passphrase, SystemKey, SystemPlaintext };
        foreach (var entry in logger.Entries)
        {
            foreach (var secret in forbidden)
            {
                entry.Should().NotContain(secret, "no secret material may ever be logged");
            }
        }
    }

    /// <summary>Captures every formatted log line for content assertions.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Enqueue(formatter(state, exception));
            Entries.Enqueue($"{state}");
        }
    }
}