using System.Runtime.CompilerServices;
using System.Text;

namespace EmailAgent.Connectors.OpenAI.Http;

/// <summary>
/// Minimal server-sent-events reader for the OpenAI-compatible streaming
/// response (M3T1 step 5): one <see cref="SseFrame"/> per blank-line-separated
/// event chunk, no assumptions about the HTTP framing around it.
/// </summary>
/// <remarks>
/// <para>
/// The state machine is the SSE one: <c>event:</c> and <c>data:</c> fields
/// accumulate until a blank line dispatches them; lines starting with
/// <c>:</c> are comments and are skipped; a multi-line <c>data</c> payload is
/// joined with <c>\n</c> (the SSE spec's separator — for the canonical
/// single-line OpenAI/LiteLLM frame the value is exactly what followed
/// <c>data: </c>); a field line without a colon is ignored per spec. Pending
/// fields at end-of-stream are dispatched rather than dropped, so a provider
/// that omits the final blank line still delivers its last frame.
/// </para>
/// <para>
/// <see cref="CancellationToken"/> is checked before every read and before
/// every <c>yield return</c> (task step 5): a cancelled caller ends the
/// enumeration with <see cref="OperationCanceledException"/> immediately
/// instead of draining the rest of the stream.
/// </para>
/// </remarks>
public static class SseStreamParser
{
    private const string EventField = "event:";
    private const string DataField = "data:";

    /// <summary>Parses <paramref name="stream"/> as SSE until it ends or <paramref name="ct"/> fires.</summary>
    /// <param name="stream">The response body stream; ownership stays with the caller (the reader leaves it open).</param>
    /// <param name="ct">Cancellation — checked between lines and before each yield.</param>
    public static async IAsyncEnumerable<SseFrame> ParseAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(
            stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

        string? eventField = null;
        List<string> dataLines = [];

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                break; // end of stream
            }

            if (line.Length == 0)
            {
                if (HasContent(eventField, dataLines))
                {
                    ct.ThrowIfCancellationRequested();
                    yield return new SseFrame(eventField ?? string.Empty, string.Join("\n", dataLines));
                    eventField = null;
                    dataLines.Clear();
                }

                continue;
            }

            if (line[0] == ':')
            {
                continue; // comment line
            }

            if (line.StartsWith(EventField, StringComparison.Ordinal))
            {
                eventField = ValueAfter(line, EventField);
            }
            else if (line.StartsWith(DataField, StringComparison.Ordinal))
            {
                dataLines.Add(ValueAfter(line, DataField));
            }

            // Any other field line (id:, retry:, or a field with no colon) is
            // not used by the OpenAI-compatible shape — ignored per spec.
        }

        if (HasContent(eventField, dataLines))
        {
            ct.ThrowIfCancellationRequested();
            yield return new SseFrame(eventField ?? string.Empty, string.Join("\n", dataLines));
        }
    }

    private static bool HasContent(string? eventField, List<string> dataLines) =>
        eventField is not null || dataLines.Count > 0;

    /// <summary>
    /// The field value after <paramref name="prefix"/> — dropping exactly one
    /// optional leading space (the SSE spec removes one, not all).
    /// </summary>
    private static string ValueAfter(string line, string prefix)
    {
        var value = line.Substring(prefix.Length);
        return value.StartsWith(' ') ? value.Substring(1) : value;
    }
}
