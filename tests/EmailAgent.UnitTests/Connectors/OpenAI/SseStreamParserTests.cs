using System.Text;
using EmailAgent.Connectors.OpenAI.Http;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Connectors.OpenAI;

/// <summary>
/// M3T1 step 5 matrix for <see cref="SseStreamParser"/>: the committed
/// streaming fixture parses into exactly three frames in order, comment lines
/// are skipped per the SSE spec, multi-line data joins with <c>\n</c>, a
/// pending frame at end-of-stream is flushed rather than dropped, and a
/// pre-cancelled token ends the enumeration with
/// <see cref="OperationCanceledException"/> (the parser checks <c>ct</c> before
/// every read and every yield).
/// </summary>
[Trait("Category", "M3T1")]
public class SseStreamParserTests
{
    [Fact]
    public async Task SseStreamParser_ParsesFixture_HappyStreaming()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            OpenAIFixtureFiles.Read("happy-streaming.sse.txt")));

        List<SseFrame> frames = await ParseAsync(stream, CancellationToken.None);

        frames.Should().HaveCount(3, "the fixture carries exactly three data frames");
        frames.Should().OnlyContain(
            f => f.Event == string.Empty, "the OpenAI shape sends no event: field");
        frames[0].Data.Should().Contain("\"content\":\"hello\"");
        frames[1].Data.Should().Contain("\"content\":\" world\"");
        frames[2].Data.Should().Contain("\"finish_reason\":\"stop\"");
        frames.Should().OnlyContain(
            f => !f.Data.Contains('\n'), "each canonical frame is a single data line");
    }

    [Fact]
    public async Task SseStreamParser_SkipsCommentLines()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            ": keep-alive comment\n"
            + "\n"
            + ": another comment\n"
            + "data: {\"a\":1}\n"
            + "\n"));

        List<SseFrame> frames = await ParseAsync(stream, CancellationToken.None);

        frames.Should().ContainSingle();
        frames[0].Data.Should().Be("{\"a\":1}", "a comment-only chunk dispatches nothing");
    }

    [Fact]
    public async Task SseStreamParser_JoinsMultiLineData_WithSpecSeparator()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            "event: chunk\n"
            + "data: line one\n"
            + "data: line two\n"
            + "\n"));

        List<SseFrame> frames = await ParseAsync(stream, CancellationToken.None);

        frames.Should().ContainSingle();
        frames[0].Event.Should().Be("chunk");
        frames[0].Data.Should().Be("line one\nline two",
            "the SSE spec joins multi-line data with a single \\n and no trailing separator");
    }

    [Fact]
    public async Task SseStreamParser_FlushesPendingFrame_AtEndOfStream()
    {
        // A provider that omits the final blank line must not lose its last frame.
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: {\"last\":true}"));

        List<SseFrame> frames = await ParseAsync(stream, CancellationToken.None);

        frames.Should().ContainSingle();
        frames[0].Data.Should().Be("{\"last\":true}");
    }

    [Fact]
    public async Task SseStreamParser_RemovesExactlyOneLeadingSpace_AfterPrefix()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            "data:{\"nospace\":true}\n"
            + "\n"
            + "data: {\"onespace\":true}\n"
            + "\n"));

        List<SseFrame> frames = await ParseAsync(stream, CancellationToken.None);

        frames.Should().HaveCount(2);
        frames[0].Data.Should().Be("{\"nospace\":true}");
        frames[1].Data.Should().Be("{\"onespace\":true}");
    }

    [Fact]
    public async Task SseStreamParser_HonorsCancellation()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: {\"a\":1}\n\n"));
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Func<Task> act = async () => await ParseAsync(stream, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "the parser checks ct before the first read and before every yield (task step 5)");
    }

    private static async Task<List<SseFrame>> ParseAsync(Stream stream, CancellationToken ct)
    {
        List<SseFrame> frames = [];
        await foreach (SseFrame frame in SseStreamParser.ParseAsync(stream, ct))
        {
            frames.Add(frame);
        }

        return frames;
    }
}
