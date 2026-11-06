using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmailAgent.Core.Options;
using EmailAgent.Infrastructure.Plugins;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EmailAgent.UnitTests.Plugins;

/// <summary>
/// M2T3 (§9 / Q-O5): the per-install shared-key HMAC-SHA-256 signature the
/// loader checks before any drop-in assembly is mapped — over the assembly
/// bytes AND the companion manifest (nested review, WARNING 1), with the
/// <c>signature</c> member itself excluded from its own digest.
/// </summary>
public class PluginSignatureVerifierTests
{
    private const string TestKey = "m2t3-unit-test-plugin-signing-key-0123456789";

    /// <summary>A manifest WITHOUT a signature member — exactly what gets signed.</summary>
    private const string Manifest =
        """{"plugin_id":"vector","version":"1.0.0","assembly":"a.dll","type":"T","capabilities":[],"http_allowlist":[],"notes":"fixed test vector"}""";

    private static PluginSignatureVerifier CreateVerifier(string? key = TestKey) =>
        new(Options.Create(new PluginOptions { SigningKey = key }));

    [Fact]
    public void Verify_Hmac_Of_AssemblyBytes_And_Manifest_Matches_Signature_In_PluginJson()
    {
        var verifier = CreateVerifier();
        var assemblyBytes = Encoding.UTF8.GetBytes("pretend IL bytes of a plugin assembly");

        // The signature travels in the drop-in's companion plugin.json as
        // base64, INSIDE the manifest it covers (minus the signature member
        // itself): round-trip the full on-disk text through real JSON.
        var signatureBase64 = Convert.ToBase64String(verifier.ComputeSignature(assemblyBytes, Manifest));
        using var manifestJson = JsonDocument.Parse(
            $$"""{"plugin_id":"vector","version":"1.0.0","assembly":"a.dll","type":"T","capabilities":[],"http_allowlist":[],"notes":"fixed test vector","signature":"{{signatureBase64}}"}""");
        var fromPluginJson = Convert.FromBase64String(
            manifestJson.RootElement.GetProperty("signature").GetString()!);

        verifier.Verify(assemblyBytes, manifestJson.RootElement.GetRawText(), fromPluginJson).Should().BeTrue(
            "the manifest fields are inside the signed input while the signature member stays outside its own digest");
    }

    [Fact]
    public void Verify_TamperedAssemblyOrManifest_Returns_False()
    {
        var verifier = CreateVerifier();
        var assemblyBytes = Encoding.UTF8.GetBytes("pretend IL bytes of a plugin assembly");
        var signature = verifier.ComputeSignature(assemblyBytes, Manifest);

        var tamperedAssembly = (byte[])assemblyBytes.Clone();
        tamperedAssembly[^1] ^= 0xFF;
        verifier.Verify(tamperedAssembly, Manifest, signature).Should().BeFalse(
            "editing the DLL after signing must fail");

        var tamperedManifest = Manifest.Replace("\"vector\"", "\"vector-evil\"", StringComparison.Ordinal);
        tamperedManifest.Should().NotBe(Manifest, "the manifest edit must actually change the input");
        verifier.Verify(assemblyBytes, tamperedManifest, signature).Should().BeFalse(
            "editing capabilities/identity fields of the companion manifest after signing must fail (review WARNING 1)");

        // A forged signature over the untampered inputs must fail too.
        verifier.Verify(assemblyBytes, Manifest, SHA256.HashData(signature)).Should().BeFalse();
    }

    [Fact]
    public void Verify_MalformedManifest_Returns_False()
    {
        var verifier = CreateVerifier();
        var assemblyBytes = Encoding.UTF8.GetBytes("pretend IL bytes of a plugin assembly");
        var signature = verifier.ComputeSignature(assemblyBytes, Manifest);

        verifier.Verify(assemblyBytes, "this is not JSON at all", signature).Should().BeFalse(
            "an unparseable manifest fails closed, never throws");
        verifier.Verify(assemblyBytes, "null", signature).Should().BeFalse();

        // Duplicate members surface lazily as ArgumentException
        // (dotnet/runtime#70604) — Verify swallows that too and fails
        // closed; "never throws" covers malformed input of every shape.
        verifier.Verify(
            assemblyBytes,
            """{"plugin_id":"vector","plugin_id":"vector-evil","version":"1.0.0","assembly":"a.dll","type":"T","capabilities":[],"http_allowlist":[]}""",
            signature)
            .Should().BeFalse("duplicate JSON members must fail closed, not throw");
    }

    [Fact]
    public void Fixed_Vector_Signature_Is_Stable()
    {
        // Fixed input vector: the expected value is recomputed HERE from the
        // BCL primitives with the scheme written out explicitly (HMAC-SHA256
        // over SHA256(assembly) || SHA256(manifest), raw manifest bytes —
        // this vector has no signature member and is already canonical), so
        // any drift in PluginSignatureVerifier's composition, canonical
        // serialization, or algorithm choice fails this test.
        var verifier = CreateVerifier();
        var assemblyBytes = Encoding.UTF8.GetBytes("pretend IL bytes of a plugin assembly");
        var manifestBytes = Encoding.UTF8.GetBytes(Manifest);

        var message = SHA256.HashData(assemblyBytes)
            .Concat(SHA256.HashData(manifestBytes))
            .ToArray();
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(TestKey), message);

        verifier.ComputeSignature(assemblyBytes, Manifest).Should().Equal(expected);
    }

    [Fact]
    public void Verify_Canonicalization_Strips_Signature_And_Normalizes_Whitespace_But_Authenticates_Every_Other_Member()
    {
        var verifier = CreateVerifier();
        var assemblyBytes = Encoding.UTF8.GetBytes("pretend IL bytes of a plugin assembly");
        var signature = verifier.ComputeSignature(assemblyBytes, Manifest);

        // Pretty-printed copy with the signature member appended: whitespace
        // is normalized and the signature member is excluded from its own
        // digest, so the very same signature still verifies — the
        // signer/verifier canonicalization contract (parse + re-serialize
        // minus `signature`).
        var manifestObject = JsonNode.Parse(Manifest)!.AsObject();
        manifestObject["signature"] = Convert.ToBase64String(signature);
        var pretty = manifestObject.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        verifier.Verify(assemblyBytes, pretty, signature).Should().BeTrue(
            "whitespace differences and the signature member itself must not change the digest");

        // Every member besides `signature` is inside the digest: appending one
        // the signer never saw invalidates the signature.
        var extended = JsonNode.Parse(Manifest)!.AsObject();
        extended["extra_member"] = "added after signing";
        extended["signature"] = Convert.ToBase64String(signature);
        verifier.Verify(assemblyBytes, extended.ToJsonString(), signature).Should().BeFalse(
            "a member the signer never committed to must fail closed");

        // Member ORDER is part of the digest (documented fail-closed choice):
        // moving plugin_id to the end changes the canonical bytes.
        var reordered = JsonNode.Parse(Manifest)!.AsObject();
        var pluginIdNode = reordered["plugin_id"]!;
        reordered.Remove("plugin_id");
        reordered["plugin_id"] = pluginIdNode;
        verifier.Verify(assemblyBytes, reordered.ToJsonString(), signature).Should().BeFalse(
            "re-ordering members invalidates the signature — fail-closed, never a bypass");
    }

    [Fact]
    public void Verify_MissingKey_Throws_OptionsValidationException()
    {
        // Missing key: the same ValidateOnStart rule (PluginOptions.
        // IsValidSigningKey) that the host enforces at boot.
        Action missingKey = () => CreateVerifier(key: null);
        missingKey.Should().Throw<OptionsValidationException>()
            .WithMessage($"*{PluginOptions.InvalidKeyMessage}*");

        // Shorter than MinSigningKeyBytes — same rule, same exception.
        Action shortKey = () => CreateVerifier(key: new string('k', PluginOptions.MinSigningKeyBytes - 1));
        shortKey.Should().Throw<OptionsValidationException>()
            .WithMessage($"*{PluginOptions.InvalidKeyMessage}*");
    }
}
