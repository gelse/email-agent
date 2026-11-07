using System.Security.Cryptography;
using System.Text;
using EmailAgent.Api.Models;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Models;

// ---------------------------------------------------------------------------
// M3T6 (SQ-M3-4 / Q-O17a): prompt_sha256 is SHA-256("{system_prompt}\n
// {user_prompt}") as 64-char LOWERCASE hex — the audit row's correlation handle
// that must never be the prompt itself. The formula is asserted against an
// INDEPENDENT SHA-256 in the test, not against the implementation's own
// arithmetic, so a change to the separator or the encoding fails here first.
// ---------------------------------------------------------------------------

[Trait("Category", "M3T6")]
public class PromptHashTests
{
    private const string SystemPrompt = "You are helpful.";
    private const string UserPrompt = "Say hello.";

    [Fact]
    public void HashIsStableForSameInput()
    {
        var first = TestCompletionService.ComputePromptHash(SystemPrompt, UserPrompt);
        var second = TestCompletionService.ComputePromptHash(SystemPrompt, UserPrompt);

        first.Should().Be(second, "the same prompt pair must hash to the same audit value");
    }

    [Fact]
    public void HashIsLowercaseHex()
    {
        var hash = TestCompletionService.ComputePromptHash(SystemPrompt, UserPrompt);

        hash.Should().HaveLength(64, "SHA-256 is 32 bytes = 64 hex characters");
        hash.Should().MatchRegex(
            "^[0-9a-f]{64}$", "the audit row stores LOWERCASE hex (Convert.ToHexString + ToLowerInvariant)");
    }

    [Fact]
    public void DifferentSystemPrompt_ProducesDifferentHash()
    {
        var baseline = TestCompletionService.ComputePromptHash(SystemPrompt, UserPrompt);
        var other = TestCompletionService.ComputePromptHash("You are unhelpful.", UserPrompt);

        other.Should().NotBe(baseline, "the system prompt is part of the hashed input");
    }

    [Fact]
    public void DifferentUserPrompt_ProducesDifferentHash()
    {
        var baseline = TestCompletionService.ComputePromptHash(SystemPrompt, UserPrompt);
        var other = TestCompletionService.ComputePromptHash(SystemPrompt, "Say goodbye.");

        other.Should().NotBe(baseline, "the user prompt is part of the hashed input");
    }

    /// <summary>
    /// The documented formula, re-derived with the BCL here: the hash is
    /// SHA-256 over UTF-8 bytes of <c>{system}\n{user}</c> — the newline
    /// separator is positional, so ("", "ab") and ("ab", "") are distinct
    /// inputs rather than one collapsed pair.
    /// </summary>
    [Fact]
    public void HashIsSha256OfSystemNewlineUser()
    {
        var expected = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes($"{SystemPrompt}\n{UserPrompt}")))
            .ToLowerInvariant();

        TestCompletionService.ComputePromptHash(SystemPrompt, UserPrompt)
            .Should().Be(expected, "SQ-M3-4: SHA-256(system_prompt || \"\\n\" || user_prompt)");

        TestCompletionService.ComputePromptHash(string.Empty, "ab")
            .Should().NotBe(
                TestCompletionService.ComputePromptHash("ab", string.Empty),
                "the separator sits between the two prompts — the order is not interchangeable");
    }
}
