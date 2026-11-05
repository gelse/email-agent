using System.Text.Json.Serialization;

namespace EmailAgent.Contracts.MessageSources;

/// <summary>
/// Attachment metadata carried with a <see cref="RemoteMessage"/> (§9;
/// M2T1 Scope / Q-C5 — metadata only; the bytes stay in blob storage).
/// Every member is <c>[JsonRequired]</c> — partial input fails loudly.
/// </summary>
public record AttachmentMetadata(
    [property: JsonRequired, JsonPropertyName("filename")] string Filename,
    [property: JsonRequired, JsonPropertyName("mime_type")] string MimeType,
    [property: JsonRequired, JsonPropertyName("size")] long Size);
