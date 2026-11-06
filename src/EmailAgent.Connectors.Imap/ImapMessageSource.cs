using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using EmailAgent.Contracts;
using EmailAgent.Contracts.MessageSources;
using EmailAgent.Contracts.Schema;
using Json.Schema;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;

namespace EmailAgent.Connectors.Imap;

/// <summary>
/// M2T4 — the DEFAULT IMAP source connector (§9 / §5). Connects to an IMAP server,
/// authenticates with username + password, opens the configured mailbox (default
/// <c>INBOX</c>), yields every message whose UID is greater than the checkpoint's
/// <c>last_uid</c> in ascending UID order, and advances the checkpoint to the highest
/// UID processed. <see cref="ValidateConfigAsync"/> performs the lightweight
/// reachability + credential check without persisting anything. No LLM call anywhere:
/// the plugin only fetches (§10; the engine call is M3/M5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Test seam.</b> MailKit's <see cref="ImapClient"/> is a concrete class, so every
/// network touch goes through <see cref="IImapClientFactory"/> (M2T4 Risks
/// "ImapClient is not mockable"): unit tests inject a factory that never yields a
/// client and therefore proves a code path is network-free, the integration suite
/// injects a protocol-logging client to assert exactly which IMAP commands were sent.
/// </para>
/// <para>
/// <b>Connection lifetime.</b> One connection per call — pooling is M5's worker-pool
/// job and is explicitly out of scope here. The client created for a call is
/// disconnected on the happy path, and the <c>finally</c> block disconnects
/// (best-effort) and disposes it on every other path, including cancellation.
/// </para>
/// <para>
/// <b>Trust.</b> The factory uses the system trust store — no trust-all callback
/// exists anywhere in this plugin (see <see cref="MailKitImapClientFactory"/>).
/// </para>
/// </remarks>
public sealed class ImapMessageSource : IMessageSource
{
    /// <summary>The items every listing fetch needs: identity, timestamp, envelope, structure and flags.</summary>
    private const MessageSummaryItems SummaryItems =
        MessageSummaryItems.UniqueId
        | MessageSummaryItems.InternalDate
        | MessageSummaryItems.Envelope
        | MessageSummaryItems.BodyStructure
        | MessageSummaryItems.Flags;

    private readonly IImapClientFactory _factory;

    /// <summary>
    /// Loader entry point: <c>PluginLoader</c> instantiates the plugin's declared
    /// entry type with a parameterless constructor (M2T3), so this ctor wires the
    /// production factory.
    /// </summary>
    public ImapMessageSource()
        : this(new MailKitImapClientFactory())
    {
    }

    /// <summary>
    /// Test seam — substitutes the <see cref="ImapClient"/> source (M2T4 Risks
    /// "ImapClient is not mockable"). Never reachable from the plugin loader.
    /// </summary>
    internal ImapMessageSource(IImapClientFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <summary>
    /// .NET ships only the Unicode/ASCII code pages unless the full code-page provider
    /// is registered (no Windows code pages on Linux by default); without it a
    /// <c>windows-1252</c>/<c>koi8-r</c> message body would fall back to Latin-1 and
    /// mojibake. Registration is process-wide, idempotent, and runs before the first
    /// instance (and therefore before any body decode).
    /// </summary>
    static ImapMessageSource() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <inheritdoc />
    public string TypeId => "imap";

    /// <inheritdoc />
    public JsonSchema ConfigSchema => ImapConfigSchema.Build();

    /// <summary>
    /// Schema-validates the document first, then connects and authenticates
    /// (network check only — nothing is written anywhere).
    /// </summary>
    /// <remarks>
    /// Result buckets, in order: schema failure (returned untouched so M2T5 can show
    /// field-level <c>issues</c>), <c>authentication failed: …</c>,
    /// <c>connection failed: …</c>, <c>unexpected error: …</c>. A cancellation
    /// requested by the caller is rethrown rather than swallowed into a failure.
    /// </remarks>
    /// <inheritdoc />
    public async Task<ValidationResult> ValidateConfigAsync(JsonElement config, CancellationToken ct)
    {
        // Schema first: ConfigSchema is the contract, and a document it rejects must
        // fail without opening a socket (IMessageSource doc; M2T5 test-connection).
        var schemaResult = JsonSchemaValidator.Validate(ConfigSchema, config);
        if (!schemaResult.Ok)
        {
            return schemaResult;
        }

        ImapConfig cfg;
        try
        {
            cfg = ImapConfig.FromJson(config);
        }
        catch (JsonException ex)
        {
            // Unreachable through the schema gate; still a validation outcome, not a crash.
            return ValidationResult.Failure($"invalid configuration: {ex.Message}");
        }

        try
        {
            // Inside the try on purpose: a factory that throws (or any failure before
            // a client exists) must land in the "unexpected error" bucket, not escape.
            using ImapClient client = _factory.Create();

            await client.ConnectAsync(cfg.Host, cfg.Port, SocketOptionsFor(cfg), ct);
            await client.AuthenticateAsync(cfg.Username, cfg.Password, ct);
            await client.DisconnectAsync(true, ct);
            return ValidationResult.Success();
        }
        catch (AuthenticationException ex)
        {
            // Wrong credentials: MailKit turns the IMAP NO on LOGIN into this type.
            return ValidationResult.Failure($"authentication failed: {ex.Message}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            return ValidationResult.Failure($"connection failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ValidationResult.Failure($"unexpected error: {ex.Message}");
        }
    }

    /// <summary>
    /// Connects, opens the mailbox read-only and yields every message newer than
    /// <paramref name="checkpoint"/>, ascending by UID (§9; plan §Steps 5).
    /// </summary>
    /// <remarks>
    /// The listing is UID-range based, never "fetch all then filter": an empty
    /// checkpoint lists the whole mailbox, a populated one fetches the open range
    /// <c>[last_uid+1 … uint.MaxValue]</c> so an already-processed UID can never be
    /// yielded twice (M2T4 acceptance "honors the checkpoint's LastUid").
    /// </remarks>
    /// <inheritdoc />
    public async IAsyncEnumerable<RemoteMessage> ListNewMessagesAsync(
        JsonElement config,
        SourceCheckpoint checkpoint,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Unlike ValidateConfigAsync this path is fed a persisted (schema-checked)
        // document, so it goes straight to the typed parse — a malformed document
        // surfaces as JsonException to the caller (plan §Steps 5).
        var cfg = ImapConfig.FromJson(config);
        ImapClient client = _factory.Create();

        try
        {
            await client.ConnectAsync(cfg.Host, cfg.Port, SocketOptionsFor(cfg), ct);
            await client.AuthenticateAsync(cfg.Username, cfg.Password, ct);

            var folder = await client.GetFolderAsync(cfg.Mailbox, ct);
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);

            foreach (var summary in await FetchSummariesAsync(folder, checkpoint, ct))
            {
                ct.ThrowIfCancellationRequested();

                var headers = await FetchHeadersAsync(folder, summary, ct);
                var bodyText = await FetchBodyTextAsync(folder, summary, ct);
                var attachments = ImapAttachmentMetadataExtractor.Extract(summary.Body);

                yield return new RemoteMessage(
                    RemoteUid: summary.UniqueId.ToString(),
                    ReceivedRemoteAt: summary.InternalDate
                        ?? summary.Envelope?.Date
                        ?? DateTimeOffset.UtcNow,
                    Headers: headers,
                    BodyText: bodyText,
                    AttachmentsMetadata: attachments);
            }

            await client.DisconnectAsync(true, ct);
        }
        finally
        {
            try
            {
                if (client.IsConnected)
                {
                    // Best-effort goodbye: the loop above may have failed or been
                    // cancelled, and a dead peer's IOException must not replace the
                    // in-flight exception (or a caller's cancellation) — nor may it
                    // skip the Dispose below.
                    await client.DisconnectAsync(true, CancellationToken.None);
                }
            }
            catch (Exception)
            {
                // Teardown is best-effort by contract (class remarks): any failure
                // here is swallowed so Dispose always runs.
            }
            finally
            {
                client.Dispose();
            }
        }
    }

    /// <summary>
    /// Advances the high-water mark to the largest UID in <paramref name="processed"/>;
    /// an empty batch leaves the checkpoint untouched (plan §Steps 6).
    /// </summary>
    /// <remarks>
    /// Config and <paramref name="ct"/> are intentionally unused: the computation is
    /// pure and must be usable without a connection (unit-tested as
    /// <c>AdvanceCheckpointAsync_PureLogic_MaxUidComputation</c>).
    /// </remarks>
    /// <inheritdoc />
    public Task<SourceCheckpoint> AdvanceCheckpointAsync(
        JsonElement config,
        SourceCheckpoint checkpoint,
        IReadOnlyList<RemoteMessage> processed,
        CancellationToken ct)
    {
        if (processed.Count == 0)
        {
            return Task.FromResult(checkpoint);
        }

        var maxUid = processed.Max(m =>
            uint.Parse(m.RemoteUid, CultureInfo.InvariantCulture));

        return Task.FromResult(new SourceCheckpoint(
            maxUid.ToString(CultureInfo.InvariantCulture),
            checkpoint.Extra));
    }

    /// <summary>
    /// The socket mode implied by <c>use_ssl</c>: implicit TLS for IMAPS (993),
    /// opportunistic STARTTLS otherwise (Greenmail's plaintext IMAP port simply
    /// never advertises STARTTLS, so the session stays plaintext).
    /// </summary>
    private static SecureSocketOptions SocketOptionsFor(ImapConfig cfg) =>
        cfg.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;

    /// <summary>
    /// One <c>UID FETCH</c>: the whole mailbox when the checkpoint is empty, else the
    /// range <c>[last_uid+1 … UniqueId.MaxValue]</c>.
    /// </summary>
    /// <remarks>
    /// Two server behaviours make the post-processing mandatory: RFC 3501 §6.4.8 says a
    /// <c>n:*</c> sequence range ALWAYS includes the newest message's UID even when
    /// <c>n</c> exceeds the mailbox maximum (so <c>12:*</c> answers with UID 11), and
    /// §6.4.8 does not require responses in any order at all. Filtering at/below the
    /// checkpoint plus sorting ascending is therefore what actually guarantees the
    /// dedupe contract and the documented UID-ordering contract — whatever the server
    /// does with the range.
    /// </remarks>
    private static async Task<IList<IMessageSummary>> FetchSummariesAsync(
        IMailFolder folder, SourceCheckpoint checkpoint, CancellationToken ct)
    {
        var plan = ResolveUidFetch(checkpoint.LastUid, out var fromUid);

        IList<IMessageSummary> fetched = plan switch
        {
            UidFetchPlan.WholeMailbox => await folder.FetchAsync(0, -1, SummaryItems, ct),
            UidFetchPlan.Nothing => Array.Empty<IMessageSummary>(),
            UidFetchPlan.RangeFrom => await folder.FetchAsync(
                new UniqueIdRange(new UniqueId(fromUid), UniqueId.MaxValue), SummaryItems, ct),
            _ => throw new InvalidOperationException($"Unknown UID fetch plan '{plan}'."),
        };

        return fetched
            .Where(s => s.UniqueId.Id >= fromUid)
            .OrderBy(s => s.UniqueId.Id)
            .ToList();
    }

    /// <summary>The three fetch plans a checkpoint's <c>last_uid</c> can imply.</summary>
    internal enum UidFetchPlan
    {
        /// <summary>No checkpoint: fetch the whole mailbox.</summary>
        WholeMailbox,

        /// <summary>Fetch the open range starting at <c>fromUid</c> (inclusive).</summary>
        RangeFrom,

        /// <summary>The checkpoint is <see cref="uint.MaxValue"/>: nothing can follow.</summary>
        Nothing,
    }

    /// <summary>
    /// Pure mapping from a checkpoint's <c>last_uid</c> (or its absence) to the fetch
    /// plan — extracted so the loud-failure and wrap-around policies are unit-testable
    /// without a live folder.
    /// </summary>
    /// <param name="lastUid">The checkpoint's raw <c>last_uid</c>, or null for an empty checkpoint.</param>
    /// <param name="fromUid">Inclusive lower UID bound (0 unless <see cref="UidFetchPlan.RangeFrom"/>).</param>
    /// <exception cref="FormatException">
    /// <paramref name="lastUid"/> is present but not a valid IMAP UID — fail loudly:
    /// a silently ignored value would re-fetch everything and break the
    /// UNIQUE(source_id, remote_uid) dedupe contract downstream.
    /// </exception>
    internal static UidFetchPlan ResolveUidFetch(string? lastUid, out uint fromUid)
    {
        fromUid = 0;

        if (lastUid is null)
        {
            return UidFetchPlan.WholeMailbox;
        }

        if (!uint.TryParse(lastUid, NumberStyles.None, CultureInfo.InvariantCulture, out var floor))
        {
            throw new FormatException(
                $"checkpoint last_uid '{lastUid}' is not a valid IMAP UID.");
        }

        if (floor == uint.MaxValue)
        {
            // Guard the `floor + 1` wrap-around, which would otherwise re-list the
            // entire mailbox from UID 1.
            return UidFetchPlan.Nothing;
        }

        fromUid = floor + 1;
        return UidFetchPlan.RangeFrom;
    }

    /// <summary>
    /// Fetches the message's RFC 5322 headers with a second <c>UID FETCH</c>
    /// (<c>MessageSummaryItems.Headers</c>) and flattens them into a lower-cased
    /// map; the first occurrence of a repeated field wins (plan §Steps 5).
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>> FetchHeadersAsync(
        IMailFolder folder, IMessageSummary summary, CancellationToken ct)
    {
        var fetched = await folder.FetchAsync(
            new List<UniqueId> { summary.UniqueId }, MessageSummaryItems.Headers, ct);

        var source = fetched.Count > 0 ? fetched[0].Headers : summary.Headers;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (source is not null)
        {
            foreach (var header in source)
            {
                var key = header.Field.ToLowerInvariant();
                if (!headers.ContainsKey(key))
                {
                    headers[key] = header.Value;
                }
            }
        }

        return headers;
    }

    /// <summary>
    /// Fetches the plain-text part's content with a content-only <c>UID FETCH</c>
    /// (<c>BODY.PEEK[part]</c>), decodes the part's Content-Transfer-Encoding and then
    /// its charset — never the attachment parts (Q-C5: metadata stays metadata, no
    /// attachment bytes cross the wire). A message without a text part (including an
    /// HTML-only message — MailKit never synthesises a text part from HTML) yields an
    /// empty body.
    /// </summary>
    /// <remarks>
    /// Two things happen in order here. (1) <c>BODY.PEEK[section]</c> returns the
    /// section's RAW octets — still transfer-encoded per RFC 3501 §6.4.5 — so a
    /// quoted-printable or base64 text part must be decoded before anything else
    /// (otherwise <c>Gr=C3=BC=C3=9Fe</c> would land in <c>BodyText</c>). (2) The
    /// decoded bytes are then interpreted with the part's declared charset (see
    /// <see cref="CharsetFor"/>). This path is deliberately NOT
    /// <c>GetBodyPartAsync</c>: MailKit merges that call's <c>BODY[n.MIME]</c> +
    /// <c>BODY[n]</c> responses into a text part whose content comes back null for a
    /// sub-part of a multipart message answered by Greenmail (its <c>BODY[n.MIME]</c>
    /// literal has no trailing CRLF), while the content-stream path fetches exactly one
    /// section and decodes it faithfully.
    /// </remarks>
    private static async Task<string> FetchBodyTextAsync(
        IMailFolder folder, IMessageSummary summary, CancellationToken ct)
    {
        if (summary.TextBody is null)
        {
            return string.Empty;
        }

        // MailKit reports an EMPTY part specifier for the root body part of a
        // non-multipart message. Passing it through would send BODY.PEEK[] — the
        // ENTIRE stored message including all headers (wire-probed: 380 octets vs the
        // 97 body octets of greenmail/standalone:2.0.1), which the transfer-decode
        // would then mangle into BodyText. RFC 3501 §6.4.5 defines BODY[TEXT] as the
        // section for "the message without its header", which is exactly what the
        // empty specifier is trying to say; multipart bodies keep their real part
        // specifier ("1", "2.1", …) and never hit this branch.
        var section = string.IsNullOrEmpty(summary.TextBody.PartSpecifier)
            ? "TEXT"
            : summary.TextBody.PartSpecifier;

        using var raw = await folder.GetStreamAsync(summary.UniqueId, section, ct);
        using var encoded = new MemoryStream();
        await raw.CopyToAsync(encoded, ct);

        using var decoded = new MemoryStream();
        using (var content = new MimeContent(encoded, TransferEncodingFor(summary.TextBody)))
        {
            await content.DecodeToAsync(decoded, ct);
        }

        decoded.Position = 0;
        using var reader = new StreamReader(
            decoded, CharsetFor(summary.TextBody), detectEncodingFromByteOrderMarks: false);

        return await reader.ReadToEndAsync(ct);
    }

    /// <summary>
    /// The part's Content-Transfer-Encoding parsed into a <see cref="ContentEncoding"/>
    /// (the <see cref="BodyPartBasic.ContentTransferEncoding"/> property carries the raw
    /// header token); a missing or unrecognized token passes the bytes through unchanged
    /// — an undecodable literal must not be silently mangled.
    /// </summary>
    private static ContentEncoding TransferEncodingFor(BodyPart part)
    {
        var token = (part as BodyPartBasic)?.ContentTransferEncoding;

        return !string.IsNullOrEmpty(token)
            && MimeUtils.TryParse(token!, out ContentEncoding encoding)
                ? encoding
                : ContentEncoding.Default;
    }

    /// <summary>
    /// The part's declared <c>charset</c> parameter — resolved through the code-page
    /// provider registered in the static constructor, so legacy labels such as
    /// <c>windows-1252</c> decode correctly — falling back to Latin-1 when the label is
    /// missing or unknown (Latin-1 maps every byte, so no bytes are lost to an
    /// unrecognized label).
    /// </summary>
    private static Encoding CharsetFor(BodyPart part)
    {
        var charset = part.ContentType.Parameters["charset"];

        if (charset is not null)
        {
            try
            {
                return Encoding.GetEncoding(charset);
            }
            catch (ArgumentException)
            {
                // Unknown/unsupported label — fall through to the byte-preserving default.
            }
            catch (NotSupportedException)
            {
                // Same fallback: the platform has no provider for this code page.
            }
        }

        return Encoding.Latin1;
    }

    /// <summary>
    /// Classifies transport/handshake/protocol problems as the "connection failed"
    /// bucket. MailKit 4.x has no <c>ImapException</c>, so this list is the explicit
    /// mapping: DNS and connect failures surface as <see cref="IOException"/> /
    /// <see cref="SocketException"/>, TLS problems as
    /// <see cref="SslHandshakeException"/>, server-side protocol errors as
    /// <see cref="ProtocolException"/> and its Imap subclasses.
    /// </summary>
    private static bool IsConnectionFailure(Exception ex) =>
        ex is SocketException
            or IOException
            or TimeoutException
            or ProtocolException
            or ServiceNotConnectedException
            or SslHandshakeException;
}
