using System.Text.Json;
using EmailAgent.Connectors.Imap;
using EmailAgent.Contracts;
using EmailAgent.Contracts.MessageSources;
using EmailAgent.Contracts.Schema;
using FluentAssertions;
using MailKit;
using MailKit.Net.Imap;
using MimeKit;
using Xunit;

namespace EmailAgent.UnitTests.Connectors.Imap;

/// <summary>
/// M2T4 unit matrix for the connector's pure logic (plan §Tests "Unit — no
/// network"): checkpoint advancement, the schema gate that must run before any
/// socket is opened, the plugin identity, and the BODYSTRUCTURE attachment walk.
/// Every test drives <see cref="ImapMessageSource"/> through the throwing factory
/// below, so a regression that starts to touch the network fails loudly here.
/// </summary>
[Trait("Category", "M2T4")]
public class ImapMessageSourceLogicTests
{
    private const string KnownGoodConfig =
        """{"host":"imap.example.com","port":993,"use_ssl":true,"username":"alice","password":"secret","mailbox":"INBOX"}""";

    private const string OutOfRangePortConfig =
        """{"host":"imap.example.com","port":99999,"use_ssl":true,"username":"alice","password":"secret","mailbox":"INBOX"}""";

    /// <summary>
    /// Stands in for the network: consulting it at all means a "pure logic" path has
    /// regressed into opening a connection.
    /// </summary>
    private sealed class ThrowingFactory : IImapClientFactory
    {
        public ImapClient Create() => throw new InvalidOperationException(
            "M2T4 pure-logic test: this code path must not open an IMAP connection.");
    }

    [Fact]
    public void ImapMessageSource_TypeId_IsImap_And_Exposes_Only_The_MessageSource_Contract()
    {
        var source = Source();

        source.TypeId.Should().Be("imap", "the type id equals the manifest plugin_id (M2T3)");
        typeof(ImapMessageSource).GetInterfaces().Should().BeEquivalentTo(
            new[] { typeof(IMessageSource) },
            "no engine/action surface may exist on the plugin — M2T4 makes an LLM call impossible by construction");
    }

    [Fact]
    public void ImapMessageSource_ConfigSchema_Is_The_M2T2_Schema_Instance()
    {
        Source().ConfigSchema.Should().BeSameAs(
            ImapConfigSchema.Build(),
            "M2T2's schema builder is the single source of truth (loader, API and SPA all see one document)");
    }

    [Fact]
    public async Task AdvanceCheckpointAsync_PureLogic_MaxUidComputation()
    {
        var extra = new Dictionary<string, JsonElement>
        {
            ["cursor"] = ParseJson("\"probe\""),
        };
        var checkpoint = new SourceCheckpoint("3", extra);
        var processed = new List<RemoteMessage> { Message("3"), Message("12"), Message("7") };

        var result = await Source().AdvanceCheckpointAsync(
            ParseJson(KnownGoodConfig), checkpoint, processed, CancellationToken.None);

        result.LastUid.Should().Be("12", "the checkpoint advances to the batch's highest UID");
        result.Extra.Should().BeSameAs(
            checkpoint.Extra, "plugin state carried in `extra` is passed through untouched");
    }

    [Fact]
    public async Task AdvanceCheckpointAsync_EmptyList_ReturnsExistingCheckpoint()
    {
        var checkpoint = new SourceCheckpoint("42", new Dictionary<string, JsonElement>());

        var result = await Source().AdvanceCheckpointAsync(
            ParseJson(KnownGoodConfig),
            checkpoint,
            Array.Empty<RemoteMessage>(),
            CancellationToken.None);

        result.Should().BeSameAs(checkpoint, "nothing was processed, so nothing moves");
    }

    [Fact]
    public async Task ValidateConfigAsync_SchemaInvalid_Fails_Without_The_Factory()
    {
        var result = await Source().ValidateConfigAsync(
            ParseJson(OutOfRangePortConfig), CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be("schema validation failed");
        result.Issues!.Should().ContainSingle()
            .Which.Should().Be(new ValidationIssue("/port", "maximum"));
    }

    [Fact]
    public async Task ValidateConfigAsync_SchemaValid_Reaches_The_Network_Seam()
    {
        var result = await Source().ValidateConfigAsync(
            ParseJson(KnownGoodConfig), CancellationToken.None);

        result.Ok.Should().BeFalse(
            "the throwing factory stands in for a server, proving the schema gate passes first and only then connects");
        result.Error.Should()
            .StartWith("unexpected error:").And
            .Contain("must not open an IMAP connection");
    }

    [Fact]
    public void ImapAttachmentMetadataExtractor_Skips_Parts_Without_A_FileName()
    {
        var text = new BodyPartText(new ContentType("text", "plain"), "1");

        ImapAttachmentMetadataExtractor.Extract(text).Should().BeEmpty(
            "the message's own body is not an attachment (Q-C5: metadata only)");
        ImapAttachmentMetadataExtractor.Extract(null).Should().BeEmpty(
            "a message with no BODYSTRUCTURE at all has no attachments either");
    }

    [Fact]
    public void ImapAttachmentMetadataExtractor_Walks_Nested_Multiparts()
    {
        var text = new BodyPartText(new ContentType("text", "plain"), "1");
        var html = new BodyPartText(new ContentType("text", "html"), "2.1");
        var attachment = new BodyPartBasic(
            new ContentType("application", "octet-stream") { Name = "report.bin" }, "2.2")
        {
            Octets = 42,
        };

        var alternative = new BodyPartMultipart(new ContentType("multipart", "alternative"), "2");
        alternative.BodyParts.Add(html);
        alternative.BodyParts.Add(attachment);

        var mixed = new BodyPartMultipart(new ContentType("multipart", "mixed"), string.Empty);
        mixed.BodyParts.Add(text);
        mixed.BodyParts.Add(alternative);

        ImapAttachmentMetadataExtractor.Extract(mixed).Should().ContainSingle()
            .Which.Should().Be(new AttachmentMetadata(
                "report.bin", "application/octet-stream", 42));
    }

    // ---------------------------------------------------------------------
    // ResolveUidFetch — the pure checkpoint → UID-fetch policy (review F5)
    // ---------------------------------------------------------------------

    [Fact]
    public void ResolveUidFetch_EmptyCheckpoint_Plan_Is_The_Whole_Mailbox()
    {
        ImapMessageSource.ResolveUidFetch(null, out var fromUid).Should().Be(
            ImapMessageSource.UidFetchPlan.WholeMailbox,
            "an empty checkpoint means no lower bound at all");
        fromUid.Should().Be(0, "no floor is required for a full-mailbox fetch");
    }

    [Fact]
    public void ResolveUidFetch_Checkpoint_Maps_To_The_Open_Range_Above_It()
    {
        ImapMessageSource.ResolveUidFetch("12", out var fromUid).Should().Be(
            ImapMessageSource.UidFetchPlan.RangeFrom);
        fromUid.Should().Be(
            13, "the range starts strictly above last_uid (an already-processed UID is never re-fetched)");
    }

    [Theory]
    [InlineData("0", 1u)]
    [InlineData("4294967294", 4294967295u)]
    public void ResolveUidFetch_Floor_Is_Bumped_By_One_Without_Wrapping(
        string lastUid, uint expectedFromUid)
    {
        ImapMessageSource.ResolveUidFetch(lastUid, out var fromUid).Should()
            .Be(ImapMessageSource.UidFetchPlan.RangeFrom);
        fromUid.Should().Be(expectedFromUid);
    }

    [Fact]
    public void ResolveUidFetch_MaxValue_Checkpoint_Fetches_Nothing()
    {
        ImapMessageSource.ResolveUidFetch("4294967295", out var fromUid).Should().Be(
            ImapMessageSource.UidFetchPlan.Nothing,
            "no UID can follow uint.MaxValue — the +1 wrap-around would re-list the whole mailbox");
        fromUid.Should().Be(0, "the plan 'Nothing' leaves no floor to use");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("99999999999999999999")]
    public void ResolveUidFetch_Unparseable_Checkpoint_Fails_Loudly(string lastUid)
    {
        Action act = () => ImapMessageSource.ResolveUidFetch(lastUid, out _);

        act.Should().Throw<FormatException>(
            "silently ignoring a malformed last_uid would re-fetch everything and break the dedupe contract")
            .WithMessage("*not a valid IMAP UID*");
    }

    /// <summary>The substituted-factory instance under test.</summary>
    private static ImapMessageSource Source() => new(new ThrowingFactory());

    /// <summary>Builds a minimal <see cref="RemoteMessage"/> for the given UID.</summary>
    private static RemoteMessage Message(string uid) => new(
        uid,
        DateTimeOffset.UnixEpoch,
        new Dictionary<string, string>(),
        "body",
        Array.Empty<AttachmentMetadata>());

    /// <summary>Parses a literal into an element that outlives its document.</summary>
    private static JsonElement ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
