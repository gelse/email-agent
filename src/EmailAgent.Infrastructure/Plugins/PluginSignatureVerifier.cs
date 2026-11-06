using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmailAgent.Core.Options;
using Microsoft.Extensions.Options;

namespace EmailAgent.Infrastructure.Plugins;

/// <summary>
/// M2T3 (§9 / Q-O5): per-install shared-key plugin signing. The signature in
/// a plugin's companion <c>plugin.json</c> is
/// <c>HMACSHA256(key, SHA256(dll_bytes) || SHA256(manifest_bytes))</c>,
/// compared in fixed time; the key comes from
/// <c>Plugin:SigningKey</c> (env <c>PLUGIN__SigningKey</c>, ≥ 32 UTF-8 bytes
/// — M2 SQ-3).
/// </summary>
/// <remarks>
/// The manifest is inside the signed input deliberately (M2T3 nested review,
/// WARNING 1): <c>plugin_id</c>, <c>version</c>, <c>type</c>,
/// <c>capabilities</c> and <c>http_allowlist</c> are exactly the fields the
/// approval and capability gates evaluate, so a signature over the DLL bytes
/// alone would let anyone with drop-folder write elevate an already-signed
/// plugin by editing its companion file. The signed manifest is the companion
/// text canonicalized by <see cref="SigningInput"/>: JSON re-serialized with
/// the <c>signature</c> member removed — both signer and verifier run the same
/// canonicalization, so the signature member stays outside its own digest
/// (no chicken-and-egg) while every other member is authenticated.
/// Whitespace/formatting differences are normalized away; member ORDER stays
/// part of the digest — re-ordering members invalidates the signature
/// (fail-closed, never a bypass).
/// </remarks>
public sealed class PluginSignatureVerifier
{
    private readonly byte[] _key;

    /// <exception cref="OptionsValidationException">
    /// The configured key is missing/blank or shorter than
    /// <see cref="PluginOptions.MinSigningKeyBytes"/> UTF-8 bytes — the same
    /// rule the <c>ValidateOnStart</c> binding enforces (M2T3 unit test
    /// <c>Verify_MissingKey_Throws_OptionsValidationException</c>).
    /// </exception>
    public PluginSignatureVerifier(IOptions<PluginOptions> options)
    {
        var configured = options?.Value?.SigningKey;
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new OptionsValidationException(
                PluginOptions.SectionName, typeof(PluginOptions), new[] { PluginOptions.InvalidKeyMessage });
        }

        var raw = Encoding.UTF8.GetBytes(configured);
        if (raw.Length < PluginOptions.MinSigningKeyBytes)
        {
            throw new OptionsValidationException(
                PluginOptions.SectionName, typeof(PluginOptions), new[] { PluginOptions.InvalidKeyMessage });
        }

        _key = raw;
    }

    /// <summary>
    /// Computes <c>HMACSHA256(key, SHA256(assemblyBytes) || SHA256(canonical manifest))</c>
    /// — the value a plugin's <c>signature</c> field must carry (base64).
    /// </summary>
    /// <exception cref="JsonException"><paramref name="manifestJson"/> is not parseable JSON or not a JSON value.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="manifestJson"/> contains duplicate member names, which surface lazily as
    /// <see cref="ArgumentException"/> (dotnet/runtime#70604) — <see cref="Verify"/> swallows both this and
    /// <see cref="JsonException"/> and fails closed; a caller of this method must not.
    /// </exception>
    /// <exception cref="ArgumentNullException">A parameter was <see langword="null"/>.</exception>
    public byte[] ComputeSignature(byte[] assemblyBytes, string manifestJson)
    {
        ArgumentNullException.ThrowIfNull(assemblyBytes);
        ArgumentNullException.ThrowIfNull(manifestJson);
        return HMACSHA256.HashData(_key, SigningInput(assemblyBytes, manifestJson));
    }

    /// <summary>
    /// Fixed-time comparison of <paramref name="signature"/> against the
    /// recomputed HMAC over (assembly bytes, manifest text as it is on disk).
    /// Returns <c>false</c> (never throws) on a wrong signature, a malformed
    /// byte array, or an unparseable manifest — the loader decides how to
    /// report a failed verification (<see cref="PluginSignatureException"/>).
    /// </summary>
    public bool Verify(byte[] assemblyBytes, string manifestJson, byte[] signature)
    {
        if (assemblyBytes is null || manifestJson is null || signature is null)
        {
            return false;
        }

        byte[] expected;
        try
        {
            expected = ComputeSignature(assemblyBytes, manifestJson);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // Unparseable manifest (JsonException), or duplicate member names
            // which materialize lazily as ArgumentException
            // (dotnet/runtime#70604): fail closed either way — "never throws"
            // is part of this method's contract.
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, signature);
    }

    /// <summary>
    /// <c>SHA256(assemblyBytes) || SHA256(canonical manifest)</c>: the exact
    /// bytes the HMAC covers. Canonicalization = parse the manifest JSON and
    /// re-serialize it with the <c>signature</c> member removed, so formatting
    /// (whitespace/indentation) differences between the signer's file and the
    /// on-disk companion are normalized identically on both sides while every
    /// field value stays authenticated; member order is NOT normalized — the
    /// order the signer committed to is part of the digest (fail-closed).
    /// </summary>
    private static byte[] SigningInput(byte[] assemblyBytes, string manifestJson)
    {
        var node = JsonNode.Parse(manifestJson)
            ?? throw new JsonException("plugin manifest must be a JSON object or value, was null");

        if (node is JsonObject manifestObject)
        {
            manifestObject.Remove("signature");
        }

        var assemblyDigest = SHA256.HashData(assemblyBytes);
        var manifestDigest = SHA256.HashData(Encoding.UTF8.GetBytes(node.ToJsonString()));

        var message = new byte[assemblyDigest.Length + manifestDigest.Length];
        Buffer.BlockCopy(assemblyDigest, 0, message, 0, assemblyDigest.Length);
        Buffer.BlockCopy(manifestDigest, 0, message, assemblyDigest.Length, manifestDigest.Length);
        return message;
    }
}
