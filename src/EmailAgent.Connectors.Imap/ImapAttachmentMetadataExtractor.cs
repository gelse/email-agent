using EmailAgent.Contracts.MessageSources;
using MailKit;

namespace EmailAgent.Connectors.Imap;

/// <summary>
/// Walks an IMAP <c>BODYSTRUCTURE</c> tree (a <see cref="BodyPart"/> graph, which is
/// what <c>MessageSummary.Body</c> carries) and collects one
/// <see cref="AttachmentMetadata"/> entry per part that names a file — metadata only,
/// the attachment bytes are NEVER fetched (Q-C5; plan §Steps 7).
/// </summary>
/// <remarks>
/// <c>Size</c> is the part's <see cref="BodyPartBasic.Octets"/> (<c>RFC822.SIZE</c>
/// for the whole message's body part) — no content download is needed to know it.
/// Multipart nodes are recursed depth-first so nested attachments (e.g.
/// <c>multipart/mixed</c> → <c>multipart/alternative</c> → attachment) are found;
/// parts without a file name — the message's own plain-text/HTML body — are skipped.
/// </remarks>
internal static class ImapAttachmentMetadataExtractor
{
    /// <summary>Extracts attachment metadata from a (possibly null) body structure.</summary>
    public static IReadOnlyList<AttachmentMetadata> Extract(BodyPart? bodyStructure)
    {
        if (bodyStructure is null)
        {
            return Array.Empty<AttachmentMetadata>();
        }

        var result = new List<AttachmentMetadata>();

        if (bodyStructure is BodyPartMultipart multipart)
        {
            foreach (var child in multipart.BodyParts)
            {
                result.AddRange(Extract(child));
            }
        }
        else if (bodyStructure is BodyPartBasic basic && !string.IsNullOrEmpty(basic.FileName))
        {
            // Servers report the media type in arbitrary case (Greenmail's BODYSTRUCTURE
            // says "TEXT PLAIN"); MIME types are case-insensitive but the
            // AttachmentMetadata contract is the canonical lower-case form (RFC 2045).
            result.Add(new AttachmentMetadata(
                basic.FileName,
                basic.ContentType.MimeType.ToLowerInvariant(),
                basic.Octets));
        }

        return result;
    }
}
