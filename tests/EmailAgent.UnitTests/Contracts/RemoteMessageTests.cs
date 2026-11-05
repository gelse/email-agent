using System.Text.Json;
using EmailAgent.Contracts.MessageSources;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Contracts;

/// <summary>
/// M2T1 step 15: <see cref="RemoteMessage"/> JSON round-trip with the
/// canonical SQ-1 property names (<c>remote_uid</c>, <c>received_remote_at</c>,
/// <c>headers</c>, <c>body_text</c>, <c>attachments_metadata</c>).
/// </summary>
public class RemoteMessageTests
{
    [Fact]
    public void RemoteMessage_RoundTripsJson()
    {
        var receivedAt = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(2));
        var message = new RemoteMessage(
            RemoteUid: "12345",
            ReceivedRemoteAt: receivedAt,
            Headers: new Dictionary<string, string>
            {
                ["Subject"] = "Hello",
                ["From"] = "a@example.com",
            },
            BodyText: "hi there",
            AttachmentsMetadata:
            [
                new AttachmentMetadata("a.txt", "text/plain", 10),
                new AttachmentMetadata("b.pdf", "application/pdf", 2048),
            ]);

        var json = JsonSerializer.Serialize(message);

        json.Should().Contain("\"remote_uid\":\"12345\"");
        json.Should().Contain("\"received_remote_at\":");
        json.Should().Contain("\"headers\":{");
        json.Should().Contain("\"Subject\":\"Hello\"");
        json.Should().Contain("\"From\":\"a@example.com\"");
        json.Should().Contain("\"body_text\":\"hi there\"");
        json.Should().Contain("\"attachments_metadata\":[");
        json.Should().Contain("\"filename\":\"a.txt\"");
        json.Should().Contain("\"mime_type\":\"application/pdf\"");
        json.Should().Contain("\"size\":2048");
        json.Should().NotContain("RemoteUid"); // canonical names only, never CLR names

        var back = JsonSerializer.Deserialize<RemoteMessage>(json);

        back.Should().NotBeNull();
        back!.RemoteUid.Should().Be("12345");
        back.ReceivedRemoteAt.Should().Be(receivedAt);
        back.Headers.Should().BeEquivalentTo(message.Headers);
        back.BodyText.Should().Be("hi there");
        back.AttachmentsMetadata.Should().Equal(message.AttachmentsMetadata);
    }

    [Fact]
    public void RemoteMessage_EmptyHeaders_And_MultipleAttachments_RoundTrip()
    {
        var message = new RemoteMessage(
            RemoteUid: "7",
            ReceivedRemoteAt: DateTimeOffset.UnixEpoch,
            Headers: new Dictionary<string, string>(),
            BodyText: string.Empty,
            AttachmentsMetadata:
            [
                new AttachmentMetadata("one.bin", "application/octet-stream", 1),
                new AttachmentMetadata("two.bin", "application/octet-stream", 2),
                new AttachmentMetadata("three.bin", "application/octet-stream", 3),
            ]);

        var back = JsonSerializer.Deserialize<RemoteMessage>(JsonSerializer.Serialize(message));

        back.Should().NotBeNull();
        back!.RemoteUid.Should().Be("7");
        back.ReceivedRemoteAt.Should().Be(DateTimeOffset.UnixEpoch);
        back.Headers.Should().BeEmpty();
        back.BodyText.Should().BeEmpty();
        back.AttachmentsMetadata.Should().HaveCount(3);
        back.AttachmentsMetadata.Should().Equal(message.AttachmentsMetadata);
    }

    [Fact]
    public void RemoteMessage_MissingRequiredMember_ThrowsInsteadOfNull()
    {
        // Every member is [JsonRequired]: partial input must fail loudly
        // instead of materialising null into non-nullable members.
        var act = () => JsonSerializer.Deserialize<RemoteMessage>("{\"remote_uid\":\"1\"}");

        act.Should().Throw<JsonException>();
    }
}
