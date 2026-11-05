using EmailAgent.Infrastructure.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;
using Xunit;

namespace EmailAgent.UnitTests.Identity;

/// <summary>
/// M1T5 unit tests for the pure (database-free) parts of
/// <see cref="OtpService"/>: base32 generation (length, charset, distinctness
/// — Q-A2) and the PBKDF2-SHA256 salt||hash round-trip (SQ-5 / Q-A8).
///
/// The task file scheduled the service-level Verify/Regenerate/Issue cases
/// here too; they move to
/// <c>EmailAgent.IntegrationTests.Identity.OtpServiceIntegrationTests</c> for
/// the same reason M1T4's refresh-store cases did: the EF in-memory provider
/// cannot host <c>EmailAgentDbContext</c> (User.KdfParams/JsonDocument and
/// the inet IPAddress columns are provider-specific types it refuses to map)
/// and the single-use semantics need real READ COMMITTED row locking to be
/// meaningful. All of the task's original assertions are preserved there.
/// </summary>
public class OtpServiceTests
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    // ------------------------------------------------------- generation

    [Fact]
    public void Generate_Returns_16Chars_From_Base32_Alphabet_Over1000Iterations()
    {
        for (var i = 0; i < 1000; i++)
        {
            var otp = OtpService.GenerateRawOtp();

            otp.Should().HaveLength(16, "Q-A2 fixes the OTP at 16 base32 chars (80 bits)");
            otp.Should().NotContain("=", "the encoder emits no '=' padding (10 bytes = 16 exact chars)");
            otp.All(Base32Alphabet.Contains)
                .Should().BeTrue(
                    "every character must come from the URL-safe base32 alphabet A–Z2–7 (Q-A2)");
            otp.Any(char.IsLower)
                .Should().BeFalse("the URL-safe base32 alphabet is uppercase-only");
        }
    }

    [Fact]
    public void Generate_TwoOTPs_Are_Distinct_Over10000Iterations()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < 10_000; i++)
        {
            seen.Add(OtpService.GenerateRawOtp());
        }

        seen.Should().HaveCount(
            10_000,
            "80 bits of CSPRNG entropy must not collide over 10 000 draws");
    }

    // ---------------------------------------------------------- hashing

    [Fact]
    public void HashOtp_Then_VerifyHash_Returns_True()
    {
        var raw = OtpService.GenerateRawOtp();

        var stored = OtpService.HashOtp(raw);

        // salt (16) || hash (32) = 48 bytes. (The task file's parenthetical
        // "(64 bytes total)" is an arithmetic slip — its own step code is
        // GetBytes(16) + 32-byte PBKDF2 output, and SQ-5 specifies a
        // 16-byte salt with a 32-byte digest: 16 + 32 = 48.)
        stored.Should().HaveCount(48, "salt (16) || hash (32) = 48 bytes (SQ-5 / Q-A8)");
        OtpService.VerifyHash(raw, stored)
            .Should().BeTrue("the PBKDF2-SHA256 verifier must round-trip the issued OTP");
    }

    [Fact]
    public void VerifyHash_WrongOtp_ReturnsFalse()
    {
        var raw = OtpService.GenerateRawOtp();
        var stored = OtpService.HashOtp(raw);

        var wrong = OtpService.GenerateRawOtp();

        OtpService.VerifyHash(wrong, stored)
            .Should().BeFalse("a different OTP must not verify against the stored hash");
    }

    [Fact]
    public void HashOtp_Uses_PerOtp_Salt_And_FailsClosed_On_MalformedStoredHash()
    {
        var raw = OtpService.GenerateRawOtp();

        var first = OtpService.HashOtp(raw);
        var second = OtpService.HashOtp(raw);

        first.Should().NotEqual(second, "each OTP gets a fresh 16-byte salt");
        first.AsSpan(0, 16).ToArray().Should().NotEqual(
            second.AsSpan(0, 16).ToArray(),
            "the salt is the first 16 stored bytes and must differ per issuance");

        // Layout: salt || hash — recomputing with the stored salt reproduces
        // the stored digest.
        var recomputed = KeyDerivation.Pbkdf2(
            raw,
            first.AsSpan(0, 16).ToArray(),
            KeyDerivationPrf.HMACSHA256,
            100_000,
            32);
        first.AsSpan(16, 32).ToArray().Should().Equal(recomputed);

        // Fail-closed: a truncated/corrupt row is a miss, never an exception.
        OtpService.VerifyHash(raw, first.AsSpan(0, 32).ToArray())
            .Should().BeFalse("a malformed otp_hash must fail closed");
        OtpService.VerifyHash(raw, Array.Empty<byte>())
            .Should().BeFalse("an empty otp_hash must fail closed");
    }

    // --------------------------------------------------------- encoding

    [Fact]
    public void Base32Encode_TenBytes_ProduceExactly16Chars_NoPadding()
    {
        // 10 bytes = 80 bits = 16 × 5-bit groups — the exact OTP shape.
        var data = Enumerable.Range(0, 10).Select(i => (byte)i).ToArray();

        var encoded = OtpService.Base32Encode(data);

        encoded.Should().HaveLength(16);
        encoded.All(Base32Alphabet.Contains).Should().BeTrue("only base32 alphabet chars, no padding");

        // Deterministic encoder: same bytes, same string.
        OtpService.Base32Encode(data).Should().Be(encoded);

        // All-zero input maps to all 'A' — pins the alphabet's first symbol.
        OtpService.Base32Encode(new byte[10]).Should().Be(new string('A', 16));
    }
}
