using System.Text.Json.Serialization;

namespace EmailAgent.Contracts.MessageSources;

/// <summary>
/// A message fetched from a source connector (§9 "Connector DTO shapes";
/// M2 SQ-1). <c>RemoteUid</c> is the source's stable id (IMAP UID as a
/// string), <c>ReceivedRemoteAt</c> is the IMAP <c>INTERNALDATE</c>,
/// <c>Headers</c> is the RFC 5322 header map, <c>BodyText</c> is the plain
/// text body, and <c>AttachmentsMetadata</c> is metadata-only per Q-C5 — the
/// bytes themselves stay in blob storage.
/// </summary>
/// <remarks>
/// Every member is <c>[JsonRequired]</c>: deserialization fails loudly on
/// partial input instead of materialising <see langword="null"/> into
/// non-nullable members. Record equality over <c>Headers</c> and
/// <c>AttachmentsMetadata</c> is by reference — compare contents, not <c>==</c>.
/// </remarks>
public record RemoteMessage(
    [property: JsonRequired, JsonPropertyName("remote_uid")] string RemoteUid,
    [property: JsonRequired, JsonPropertyName("received_remote_at")] DateTimeOffset ReceivedRemoteAt,
    [property: JsonRequired, JsonPropertyName("headers")] IReadOnlyDictionary<string, string> Headers,
    [property: JsonRequired, JsonPropertyName("body_text")] string BodyText,
    [property: JsonRequired, JsonPropertyName("attachments_metadata")] IReadOnlyList<AttachmentMetadata> AttachmentsMetadata);
