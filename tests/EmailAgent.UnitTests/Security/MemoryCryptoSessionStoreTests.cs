using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using EmailAgent.Core.Security;
using EmailAgent.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace EmailAgent.UnitTests.Security;

/// <summary>
/// M5T1 unit tests for <see cref="MemoryCryptoSessionStore"/> (SQ-M5-1 /
/// SQ-M5-6): the token-hash-only custody, the 401-vs-403 statuses, sliding
/// idle vs hard absolute timeouts, the idempotent replace, revocation, and
/// the structural no-persistence-path property. Fakes only — no DB, no
/// external service (AGENTS.md hard constraint 3).
/// </summary>
/// <remarks>
/// Timing tests use real <see cref="DateTimeOffset.UtcNow"/> (the store's
/// clock, per design) with asymmetric margins: expiry assertions only sleep
/// past the deadline (a longer sleep can only strengthen them), while the
/// one liveness assertion after <c>Touch</c> keeps a sub-second overrun
/// budget of its own.
/// </remarks>
public class MemoryCryptoSessionStoreTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>The canonical 32-byte test DEK (never key material of any real account).</summary>
    private static readonly byte[] Dek = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private static MemoryCryptoSessionStore CreateStore(
        TimeSpan? idle = null, TimeSpan? absolute = null, IMemoryCache? cache = null)
        => new(
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new CryptoSessionOptions
            {
                IdleTimeout = idle ?? TimeSpan.FromMinutes(30),
                AbsoluteTimeout = absolute ?? TimeSpan.FromHours(12),
            }));

    // ----------------------------------------------------------------- create / token

    [Fact]
    public void CreateOrReplace_ReturnsTokenOnce_AndHoldsOnlyHash()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = CreateStore(cache: cache);

        var handle = store.CreateOrReplace(UserId, Dek);

        handle.Token.Should().NotBeNullOrWhiteSpace("the SPA needs the opaque token exactly once");
        handle.SessionId.Should().NotBe(Guid.Empty);

        // The entry the store actually keeps (its documented user-key view,
        // MemoryCryptoSessionStore.UserCacheKey): the token appears only as
        // its SHA-256 hash and the DEK only GCM-wrapped under the session key.
        cache.TryGetValue<CryptoSession>($"crypto-session/user/{UserId:N}", out var entry)
            .Should().BeTrue("the session must be reachable for the presented-token lookup");
        entry!.TokenHash.Should().Be(
            Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(handle.Token))),
            "the store retains sha256(SK-token), never the token itself");
        entry.TokenHash.Should().NotBe(handle.Token);
        entry.DekWrappedBySk.Should().NotEqual(Dek,
            "the DEK is never held in the clear — only nonce||ct||tag under the SK");
        entry.DekWrappedBySk.Should().HaveCount(12 + 32 + 16,
            "12-byte nonce prefix + 32-byte ciphertext + 16-byte GCM tag");
        entry.SessionKey.Should().HaveCount(32, "the 256-bit session key wraps the DEK");

        // No raw token or DEK material lingers on the store instance itself.
        typeof(MemoryCryptoSessionStore)
            .GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(f => !f.IsLiteral)
            .Select(f => f.FieldType)
            .Should().NotContain(t => t == typeof(string) || t == typeof(byte[]),
                "the store keeps material in the cache entries, not in fields");
    }

    [Fact]
    public void TryGetDek_WithCorrectToken_ReturnsDek()
    {
        var store = CreateStore();
        var handle = store.CreateOrReplace(UserId, Dek);

        var result = store.TryGetDek(UserId, handle.Token);

        result.Status.Should().Be(CryptoSessionStatus.Ok);
        result.IsOk.Should().BeTrue();
        result.Dek.Should().Equal(Dek, "the wrapped DEK unwraps back to the original");
        result.SessionId.Should().Be(handle.SessionId);
    }

    [Fact]
    public void TryGetDek_WrongToken_ReturnsAbsent()
    {
        var store = CreateStore();
        var handle = store.CreateOrReplace(UserId, Dek);
        var tampered = handle.Token[..^1] + (handle.Token.EndsWith('A') ? "B" : "A");

        store.TryGetDek(UserId, tampered).Status.Should().Be(CryptoSessionStatus.Absent,
            "a token that does not authenticate the live session is Absent, not Locked");
        store.TryGetDek(UserId, string.Empty).Status.Should().Be(CryptoSessionStatus.Absent,
            "a required-but-missing credential is Absent");
        store.TryGetDek(OtherUserId, handle.Token).Status.Should().Be(CryptoSessionStatus.Absent,
            "another user has no session that this token can authenticate");
    }

    // ----------------------------------------------------------------- timeouts

    [Fact]
    public void IdleTimeout_ExpiresSession_AfterInactivity()
    {
        var store = CreateStore(idle: TimeSpan.FromMilliseconds(200));
        var handle = store.CreateOrReplace(UserId, Dek);

        Thread.Sleep(600);

        store.TryGetDek(UserId, handle.Token).Status.Should().Be(CryptoSessionStatus.Expired,
            "an idle session past SESSION__IdleTimeout reports Expired and is evicted");
        store.TryGetDekForUser(UserId).Status.Should().Be(CryptoSessionStatus.Locked,
            "the evicted session leaves the user with no session at all");
    }

    [Fact]
    public void Touch_RefreshesIdle_ButNeverPastAbsolute()
    {
        // A) Sliding refresh: the deadline moves forward by the idle window,
        //    so a request after the ORIGINAL deadline still resolves Ok
        //    (nominal 3.3 s > the 3.0 s original deadline, while the new
        //    deadline sits 3.0 s after the touch — a sub-second sleep overrun
        //    cannot flip either side).
        var sliding = CreateStore(idle: TimeSpan.FromMilliseconds(3000));
        var slidingHandle = sliding.CreateOrReplace(UserId, Dek);
        Thread.Sleep(1500);
        sliding.Touch(UserId, slidingHandle.Token).Should().BeTrue("the session is still live");
        Thread.Sleep(1800); // elapsed ≈ 3.3 s > the original 3.0 s deadline
        sliding.TryGetDek(UserId, slidingHandle.Token).Status.Should().Be(CryptoSessionStatus.Ok,
            "Touch slid the idle deadline forward");

        // B) Clamp: a touch can never push the idle deadline past the
        //    absolute cap — the session still expires at the absolute time.
        var clamped = CreateStore(idle: TimeSpan.FromSeconds(10), absolute: TimeSpan.FromMilliseconds(400));
        var clampedHandle = clamped.CreateOrReplace(UserId, Dek);
        Thread.Sleep(150);
        clamped.Touch(UserId, clampedHandle.Token).Should().BeTrue();
        Thread.Sleep(600); // past the 400 ms absolute cap
        clamped.TryGetDek(UserId, clampedHandle.Token).Status.Should().Be(CryptoSessionStatus.Expired,
            "the idle refresh was clamped to the absolute deadline");
    }

    [Fact]
    public void AbsoluteTimeout_ExpiresEvenWhenTouched()
    {
        var store = CreateStore(idle: TimeSpan.FromSeconds(10), absolute: TimeSpan.FromMilliseconds(500));
        var handle = store.CreateOrReplace(UserId, Dek);

        Thread.Sleep(150);
        store.Touch(UserId, handle.Token).Should().BeTrue("touched while still within the absolute cap");
        Thread.Sleep(700); // past SESSION__AbsoluteTimeout regardless of the touches

        store.TryGetDek(UserId, handle.Token).Status.Should().Be(CryptoSessionStatus.Expired,
            "the absolute cap is a hard deadline from creation — Touch never extends it");
    }

    // ----------------------------------------------------------------- replace / revoke

    [Fact]
    public void ReUnlock_ReplacesExistingSession()
    {
        var store = CreateStore();
        var first = store.CreateOrReplace(UserId, Dek);
        var second = store.CreateOrReplace(UserId, Dek);

        second.Token.Should().NotBe(first.Token, "each unlock mints a fresh session key");
        second.SessionId.Should().NotBe(first.SessionId);

        store.TryGetDek(UserId, first.Token).Status.Should().Be(CryptoSessionStatus.Absent,
            "the replaced session's token no longer authenticates anything");
        store.TryGetDek(UserId, second.Token).Status.Should().Be(CryptoSessionStatus.Ok,
            "exactly one live session per user — the new one");

        store.RevokeByUser(UserId).Should().Be(second.SessionId,
            "only ONE session was live, so a single revoke returns the new id");
        store.RevokeByUser(UserId).Should().BeNull("nothing is left to revoke");
    }

    [Fact]
    public void RevokeByUser_MakesSessionAbsent()
    {
        var store = CreateStore();
        var handle = store.CreateOrReplace(UserId, Dek);

        var revoked = store.RevokeByUser(UserId);

        revoked.Should().Be(handle.SessionId, "the audit row needs the revoked session id");
        store.TryGetDek(UserId, handle.Token).Status.Should().Be(CryptoSessionStatus.Absent);
        store.TryGetDekForUser(UserId).Status.Should().Be(CryptoSessionStatus.Locked);
        store.RevokeByUser(UserId).Should().BeNull("a second revoke has nothing to return");
    }

    [Fact]
    public void TryGetDekForUser_LockedWhenNoSession()
    {
        var store = CreateStore();

        var result = store.TryGetDekForUser(UserId);

        result.Status.Should().Be(CryptoSessionStatus.Locked,
            "a user with no session is Locked — the worker's transient outcome (SQ-M5-10)");
        result.Dek.Should().BeNull();
        result.SessionId.Should().BeNull();
        store.TouchForUser(UserId).Should().BeFalse("there is nothing to touch either");
    }

    // ----------------------------------------------------------------- structural / options

    [Fact]
    public void Store_HasNoDbContextDependency()
    {
        var ctor = typeof(MemoryCryptoSessionStore).GetConstructors().Single();
        var parameters = ctor.GetParameters();

        parameters.Should().HaveCount(2, "the store takes exactly the cache and the timeout options");
        parameters[0].ParameterType.Should().Be(typeof(IMemoryCache));
        parameters[1].ParameterType.Should().Be(typeof(IOptions<CryptoSessionOptions>));

        // The no-persistence-path property (SQ-M5-1): neither the store nor
        // the ICryptoSessionStore contract mentions a database context.
        var contractTypes = new[] { typeof(MemoryCryptoSessionStore), typeof(ICryptoSessionStore) }
            .SelectMany(t => t.GetMethods().SelectMany(m => m.GetParameters().Select(p => p.ParameterType)))
            .Concat(parameters.Select(p => p.ParameterType))
            .Concat(typeof(MemoryCryptoSessionStore)
                .GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(f => f.FieldType));
        contractTypes.Should().NotContain(t => t.Name.Contains("DbContext", StringComparison.Ordinal),
            "no DB parameter, field, or argument — a DB dump can only ever hold KEK-wrapped DEKs");
    }

    [Fact]
    public void CryptoSessionOptions_Defaults_Are30mAnd12h()
    {
        var options = new CryptoSessionOptions();

        options.IdleTimeout.Should().Be(TimeSpan.FromMinutes(30),
            "SESSION__IdleTimeout DEFAULT 00:30:00 (SQ-M5-6)");
        options.AbsoluteTimeout.Should().Be(TimeSpan.FromHours(12),
            "SESSION__AbsoluteTimeout DEFAULT 12:00:00 (SQ-M5-6)");
        CryptoSessionOptions.SectionName.Should().Be("Session",
            "the SESSION__* environment variables bind into this section");

        CryptoSessionOptions.IsValid(options).Should().BeTrue();
        CryptoSessionOptions.IsValid(new CryptoSessionOptions { IdleTimeout = TimeSpan.Zero })
            .Should().BeFalse("a non-positive idle timeout would mint already-expired sessions");
        CryptoSessionOptions.IsValid(new CryptoSessionOptions { AbsoluteTimeout = TimeSpan.Zero })
            .Should().BeFalse();
        CryptoSessionOptions.IsValid(null!).Should().BeFalse();
        CryptoSessionOptions.InvalidMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Unlock_DekInvalidLength_Throws()    {
        var store = CreateStore();

        var shortDek = () => store.CreateOrReplace(UserId, new byte[31]);
        var longDek = () => store.CreateOrReplace(UserId, new byte[33]);
        var emptyDek = () => store.CreateOrReplace(UserId, Array.Empty<byte>());

        shortDek.Should().Throw<ArgumentException>()
            .WithMessage("*exactly 32 bytes*").And.ParamName.Should().Be("dek");
        longDek.Should().Throw<ArgumentException>().And.ParamName.Should().Be("dek");
        emptyDek.Should().Throw<ArgumentException>().And.ParamName.Should().Be("dek");

        store.TryGetDekForUser(UserId).Status.Should().Be(CryptoSessionStatus.Locked,
            "a rejected unlock attempt must not have created a session");
    }
}
