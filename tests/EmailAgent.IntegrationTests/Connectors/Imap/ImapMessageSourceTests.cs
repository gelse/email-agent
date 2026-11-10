using System.Globalization;
using System.Text;
using System.Text.Json;
using EmailAgent.Connectors.Imap;
using EmailAgent.Contracts.MessageSources;
using EmailAgent.IntegrationTests.Fixtures;
using FluentAssertions;
using MailKit;
using MailKit.Net.Imap;
using MimeKit;
using Xunit;

namespace EmailAgent.IntegrationTests.Connectors.Imap;

/// <summary>
/// M2T4's Greenmail-backed acceptance matrix (plan §Tests "Integration — 10+ tests
/// against Testcontainers Greenmail"): validation buckets, UID-ordered listing,
/// checkpoint honouring, header/body fidelity, attachment metadata without content,
/// and checkpoint advancement.
/// </summary>
/// <remarks>
/// The tests purge the mailbox first and assert relative properties (counts,
/// ordering, <c>last_uid == max</c>) because Greenmail keeps its UID high-water mark
/// across purges — see <see cref="GreenMailFixture"/>'s remarks. Only the fixture's
/// container credentials are used; no production endpoint and no external service is
/// ever contacted (AGENTS.md hard constraints).
/// </remarks>
[Trait("Category", "M2T4")]
public sealed class ImapMessageSourceTests(GreenMailFixture greenmail) : IClassFixture<GreenMailFixture>
{
    // ---------------------------------------------------------------------
    // ValidateConfigAsync — schema gate, then connect, then authenticate
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ValidateConfigAsync_HappyPath_ReturnsSuccess()
    {
        var result = await Source().ValidateConfigAsync(
            greenmail.CreateConfig(), CancellationToken.None);

        result.Ok.Should().BeTrue(
            $"the fixture provisions alice/alice on {greenmail.ImapHost}:{greenmail.ImapPort} — got: {result.Error}");
        result.Error.Should().BeNull();
        result.Issues!.Should().BeNull();
    }

    /// <summary>
    /// Milestone row "ValidateConfigAsync detects bad credentials" — and the reason the
    /// fixture strips <c>-Dgreenmail.auth.disabled</c>: a server that accepts any
    /// password could not fail this test.
    /// </summary>
    [Fact]
    public async Task Imap_ValidateConfigAsync_WrongPassword_ReturnsValidationFailure()
    {
        var result = await Source().ValidateConfigAsync(
            greenmail.CreateConfig(password: "definitely-not-the-password"),
            CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error.Should().StartWith(
            "authentication failed", "Greenmail answers a wrong LOGIN with `NO LOGIN failed …`");
        result.Error.Should().NotContain(
            "connection failed", "a rejected credential must never be reported as a transport problem");
    }

    [Fact]
    public async Task ValidateConfigAsync_BadHost_ReturnsFailure_WithConnectionMessage()
    {
        // Nothing listens on port 1 of the loopback interface, so the connect fails
        // before any IMAP or TLS traffic — the connection bucket, not the auth one.
        var result = await Source().ValidateConfigAsync(
            greenmail.CreateConfig(host: "127.0.0.1", port: 1),
            CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error.Should().StartWith(
            "connection failed:", "an unreachable endpoint must be reported as a connection failure");
    }

    /// <summary>
    /// Guards the task's no-trust-all decision: Greenmail's IMAPS certificate is
    /// self-signed (and expired), so a connector that used the plan snippet's
    /// trust-all callback would report SUCCESS here. The system trust store must
    /// refuse the handshake instead.
    /// </summary>
    [Fact]
    public async Task ValidateConfigAsync_SslAgainstSelfSignedGreenmail_ReturnsConnectionFailure()
    {
        var result = await Source().ValidateConfigAsync(
            greenmail.CreateConfig(useSsl: true), CancellationToken.None);

        result.Ok.Should().BeFalse(
            "the IMAPS certificate is self-signed and expired, and ImapClientFactory sets no validation callback");
        result.Error.Should().StartWith(
            "connection failed:", $"a certificate failure is a transport failure, got: {result.Error}");
        result.Error.Should().NotContain(
            "refused",
            "the fixture's wait strategy holds port 3993 open, so a refused TCP connect is impossible — "
            + "the failure must come from the TLS handshake, not from nothing listening (review N4)");
    }

    // ---------------------------------------------------------------------
    // ListNewMessagesAsync — UID range, ordering, headers, body, attachments
    // ---------------------------------------------------------------------

    /// <summary>Milestone row "Service connects to IMAP": three seeded messages come back.</summary>
    [Fact]
    public async Task Imap_ListNewMessages_ConnectsAndLists()
    {
        await greenmail.PurgeMailAsync();
        await greenmail.SendAsync("first message", "body one");
        await greenmail.SendAsync("second message", "body two");
        await greenmail.SendAsync("third message", "body three");

        var messages = await ListAsync(Source(), greenmail.CreateConfig());

        messages.Should().HaveCount(
            3, "every message delivered to alice@greenmail.test since the purge must be listed");
        messages.Select(m => m.Headers["subject"]).Should().BeEquivalentTo(
            new[] { "first message", "second message", "third message" });
        messages.Select(m => m.RemoteUid).Should().OnlyHaveUniqueItems(
            "the dedupe key must be one UID per message");
    }

    /// <summary>Milestone row "UIDs are fetched in order": listing order is UID ascending.</summary>
    [Fact]
    public async Task Imap_ListNewMessages_ReturnsUidsInOrder()
    {
        await greenmail.PurgeMailAsync();
        for (var i = 1; i <= 4; i++)
        {
            await greenmail.SendAsync($"seed {i}", $"seed body {i}");
        }

        var messages = await ListAsync(Source(), greenmail.CreateConfig());
        var uids = UidsOf(messages);

        uids.Should().HaveCount(4);
        uids.Should().BeInAscendingOrder("the connector must yield messages in UID order");
        messages.Select(m => m.Headers["subject"]).Should().Equal(
            ["seed 1", "seed 2", "seed 3", "seed 4"],
            "Greenmail hands out UIDs at delivery, so UID order must be delivery order");
    }

    [Fact]
    public async Task ListNewMessagesAsync_WithCheckpoint_YieldsOnlyMessagesWithUidGreaterThanCheckpoint()
    {
        await greenmail.PurgeMailAsync();
        await greenmail.SendAsync("already fetched 1", "one");
        await greenmail.SendAsync("already fetched 2", "two");
        await greenmail.SendAsync("newer", "three");

        var config = greenmail.CreateConfig();
        var all = await ListAsync(Source(), config);
        all.Should().HaveCount(3);

        var uids = UidsOf(all);
        var checkpoint = new SourceCheckpoint(
            uids[0].ToString(CultureInfo.InvariantCulture),
            new Dictionary<string, JsonElement>());

        var remaining = await ListAsync(Source(), config, checkpoint);

        UidsOf(remaining).Should().Equal(
            [uids[1], uids[2]],
            "messages at or below last_uid must never be re-yielded");
    }

    [Fact]
    public async Task ListNewMessagesAsync_DedupeRespectsUidRange_NoMessageYieldedTwice()
    {
        await greenmail.PurgeMailAsync();
        await greenmail.SendAsync("dedupe 1", "one");
        await greenmail.SendAsync("dedupe 2", "two");
        await greenmail.SendAsync("dedupe 3", "three");

        var config = greenmail.CreateConfig();
        var first = await ListAsync(Source(), config);
        first.Should().HaveCount(3);

        var advanced = await Source().AdvanceCheckpointAsync(
            config,
            new SourceCheckpoint(null, new Dictionary<string, JsonElement>()),
            first,
            CancellationToken.None);
        advanced.LastUid.Should().Be(
            UidsOf(first).Max().ToString(CultureInfo.InvariantCulture));

        // Two consecutive fetches from the advanced checkpoint: both must be empty —
        // the UNIQUE(source_id, remote_uid) contract upstream depends on it.
        (await ListAsync(Source(), config, advanced)).Should().BeEmpty(
            "every UID is already at or below the checkpoint");
        (await ListAsync(Source(), config, advanced)).Should().BeEmpty(
            "repeating the same fetch must stay idempotent");
    }

    [Fact]
    public async Task ListNewMessagesAsync_HeadersContainFromAndSubject()
    {
        await greenmail.PurgeMailAsync();
        await greenmail.SendAsync("Header subject", "header body");

        var messages = await ListAsync(Source(), greenmail.CreateConfig());
        var message = messages.Should().ContainSingle().Which;

        message.Headers.Keys.Should()
            .Contain("from").And
            .Contain("to").And
            .Contain("subject").And
            .Contain("date");
        message.Headers["subject"].Should().Be("Header subject");
        message.Headers["from"].Should().Contain("sender@corp.test");
        message.Headers["to"].Should().Contain(GreenMailFixture.Email);
        message.Headers["date"].Should().NotBeNullOrWhiteSpace();
        message.ReceivedRemoteAt.Should().BeAfter(
            DateTimeOffset.UtcNow.AddMinutes(-30), "INTERNALDATE is the delivery time");
    }

    [Fact]
    public async Task ListNewMessagesAsync_BodyTextContainsPlainText()
    {
        await greenmail.PurgeMailAsync();
        const string marker = "PLAINTEXT-BODY-MARKER-42";
        await greenmail.SendAsync("Body subject", $"line one\n{marker}\nline three");

        var messages = await ListAsync(Source(), greenmail.CreateConfig());
        var message = messages.Should().ContainSingle().Which;

        message.BodyText.Should().Contain(
            marker, "the plain-text part is fetched and decoded");
    }

    /// <summary>
    /// Review finding F1 (CRITICAL): <c>BODY.PEEK[section]</c> returns RAW,
    /// still-transfer-encoded octets — a connector that skipped the
    /// Content-Transfer-Encoding step would ship <c>Gr=C3=BC=C3=9Fe</c> or base64
    /// garbage as <c>BodyText</c>. The seeded bodies are deliberately ASCII-only so
    /// the encoded forms are deterministic: quoted-printable escapes the <c>=</c> and
    /// soft-wraps the long tail; base64 is base64. Both must round-trip byte-exact
    /// (modulo the transport's trailing line terminator).
    /// </summary>
    [Theory]
    [InlineData(ContentEncoding.QuotedPrintable)]
    [InlineData(ContentEncoding.Base64)]
    public async Task ListNewMessagesAsync_TransferEncodedBody_RoundTripsExactly(
        ContentEncoding transferEncoding)
    {
        await greenmail.PurgeMailAsync();

        var body = transferEncoding switch
        {
            ContentEncoding.QuotedPrintable =>
                "QP marker equals = and a long tail " + new string('x', 120),
            _ => "BASE64 marker plain ascii tail",
        };

        await greenmail.SendAsync(
            $"Transfer subject {transferEncoding}", body, transferEncoding: transferEncoding);

        var messages = await ListAsync(Source(), greenmail.CreateConfig());
        var message = messages.Should().ContainSingle().Which;

        message.BodyText.TrimEnd('\r', '\n').Should().Be(
            body,
            $"Content-Transfer-Encoding {transferEncoding} must be decoded off the raw "
            + "IMAP literal BEFORE the charset pass — no '=XX' residue, no soft line "
            + "breaks, no base64 alphabet left in the body");
    }

    /// <summary>
    /// Plan test <c>ListNewMessagesAsync_AttachmentsMetadataOnly_NoContentFetched</c>,
    /// asserted against the real wire log: <c>ImapClient</c> cannot be mocked (its
    /// methods are not virtual), so the connector is driven through a factory that
    /// installs a <c>ProtocolLogger</c> and the test reads the IMAP commands off it.
    /// </summary>
    [Fact]
    public async Task ListNewMessagesAsync_AttachmentsMetadataOnly_NoContentFetched()
    {
        const string attachmentMarker = "ATTACHMENT-MARKER-9f2c-content";
        const string bodyMarker = "BODY-TEXT-MARKER-7d1e";

        await greenmail.PurgeMailAsync();
        await greenmail.SendAsync(
            "Attachment subject",
            bodyMarker,
            attachmentFileName: "report.txt",
            attachmentContent: attachmentMarker);

        var log = new MemoryStream();
        var source = new ImapMessageSource(new ProtocolLoggingFactory(log));

        var messages = await ListAsync(source, greenmail.CreateConfig());
        var message = messages.Should().ContainSingle().Which;

        message.AttachmentsMetadata.Should().ContainSingle(
            "the seeded attachment must be reported (metadata only, Q-C5)");
        message.AttachmentsMetadata[0].Filename.Should().Be("report.txt");
        message.AttachmentsMetadata[0].MimeType.Should().Be("text/plain");
        message.AttachmentsMetadata[0].Size.Should().BeGreaterThan(
            0, "BODYSTRUCTURE carries the size without fetching the bytes");
        message.BodyText.Should().Contain(bodyMarker);

        var wire = Encoding.UTF8.GetString(log.ToArray());
        wire.Should().Contain(
            "BODY.PEEK[1]", "the message's plain-text part is fetched for BodyText");
        wire.Should().NotContain(
            "BODY.PEEK[2]", "the attachment part must never be fetched");
        wire.Should().NotContain(
            attachmentMarker, "attachment bytes must not cross the wire at all");
        wire.Should().NotContain(
            Convert.ToBase64String(Encoding.UTF8.GetBytes(attachmentMarker)),
            "even if MimeKit transfer-encoded the attachment, its base64 form must not "
            + "appear on the wire either — the raw-marker check alone would be vacuous "
            + "for an encoded part (review F6)");
    }

    // ---------------------------------------------------------------------
    // AdvanceCheckpointAsync
    // ---------------------------------------------------------------------

    /// <summary>Milestone row "Checkpoint advances": last_uid becomes the batch maximum.</summary>
    [Fact]
    public async Task Imap_AdvanceCheckpoint_UpdatesLastUid()
    {
        await greenmail.PurgeMailAsync();
        await greenmail.SendAsync("checkpoint 1", "one");
        await greenmail.SendAsync("checkpoint 2", "two");
        await greenmail.SendAsync("checkpoint 3", "three");

        var config = greenmail.CreateConfig();
        var messages = await ListAsync(Source(), config);
        messages.Should().HaveCount(3);

        var checkpoint = await Source().AdvanceCheckpointAsync(
            config,
            new SourceCheckpoint(null, new Dictionary<string, JsonElement>()),
            messages,
            CancellationToken.None);

        checkpoint.LastUid.Should().Be(
            UidsOf(messages).Max().ToString(CultureInfo.InvariantCulture));
        checkpoint.Extra.Should().BeEmpty(
            "the IMAP connector keeps no state beyond last_uid");
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    /// <summary>The connector under test (production factory — every test here needs a real server).</summary>
    private static ImapMessageSource Source() => new();

    /// <summary>Enumerates a listing to a list so it can be asserted on.</summary>
    private static async Task<List<RemoteMessage>> ListAsync(
        ImapMessageSource source,
        JsonElement config,
        SourceCheckpoint? checkpoint = null)
    {
        List<RemoteMessage> messages = [];
        await foreach (var message in source.ListNewMessagesAsync(
            config, checkpoint ?? SourceCheckpoint.Empty, CancellationToken.None))
        {
            messages.Add(message);
        }

        return messages;
    }

    /// <summary>Parses the string UIDs into numbers so ordering can be asserted numerically.</summary>
    private static uint[] UidsOf(IEnumerable<RemoteMessage> messages) =>
    [
        .. messages
            .Select(m => uint.Parse(m.RemoteUid, CultureInfo.InvariantCulture))
    ];

    /// <summary>
    /// Yields a client whose IMAP conversation is written to <paramref name="log"/> —
    /// the integration-suite stand-in for the unmockable <c>ImapClient</c>.
    /// </summary>
    private sealed class ProtocolLoggingFactory(Stream log) : IImapClientFactory
    {
        public ImapClient Create() => new(new ProtocolLogger(log, leaveOpen: true));
    }
}
