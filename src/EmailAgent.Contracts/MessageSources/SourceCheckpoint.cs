using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmailAgent.Contracts.MessageSources;

/// <summary>
/// The high-water mark a source connector persists per message source
/// (§6 <c>message_sources.checkpoint_json jsonb DEFAULT '{}'</c>; §9;
/// M2T1 step 6). Canonical JSON: <c>{"last_uid":"12345","extra":{}}</c>.
/// </summary>
/// <remarks>
/// <c>Extra</c> holds plugin-specific state as <see cref="JsonElement"/>s.
/// <see cref="JsonElement"/> is read-only and tied to the lifetime of the
/// <see cref="JsonDocument"/> it came from: elements must be
/// <see cref="JsonElement.Clone"/>d before the document is disposed —
/// <see cref="FromJson"/> clones every value it reads, and callers that build
/// checkpoints by hand must do the same.
/// Both sanctioned read paths — <see cref="FromJson(JsonElement)"/> and plain
/// <c>JsonSerializer.Deserialize&lt;SourceCheckpoint&gt;</c> (routed here) —
/// share one contract, so the storage default <c>{}</c> produces an empty
/// non-null checkpoint either way; record equality over the collections is
/// by reference, so compare fields rather than using <c>==</c>.
/// </remarks>
[JsonConverter(typeof(SourceCheckpointConverter))]
public record SourceCheckpoint(
    [property: JsonPropertyName("last_uid")] string? LastUid,
    [property: JsonPropertyName("extra")] IReadOnlyDictionary<string, JsonElement> Extra)
{
    /// <summary>The canonical empty checkpoint (JSON <c>{"last_uid":null,"extra":{}}</c>).</summary>
    public static SourceCheckpoint Empty { get; } =
        new(null, ImmutableDictionary<string, JsonElement>.Empty);

    /// <summary>
    /// Parses a checkpoint element. Lenient by design: missing members are
    /// tolerated so the database default <c>{}</c> parses (missing
    /// <c>last_uid</c> → <see langword="null"/>, missing <c>extra</c> → empty)
    /// — callers that require a uid check <c>LastUid</c> themselves (M2T1
    /// step 6). Present-but-mistyped members throw <see cref="JsonException"/>,
    /// and every <c>extra</c> value is cloned so the result survives disposal
    /// of the source document.
    /// </summary>
    public static SourceCheckpoint FromJson(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw new JsonException($"checkpoint must be a JSON object, was {el.ValueKind}");

        string? lastUid = null;
        if (el.TryGetProperty("last_uid", out var lastUidEl))
        {
            lastUid = lastUidEl.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => lastUidEl.GetString(),
                _ => throw new JsonException("checkpoint field 'last_uid' must be a string or null"),
            };
        }

        IReadOnlyDictionary<string, JsonElement> extra = ImmutableDictionary<string, JsonElement>.Empty;
        if (el.TryGetProperty("extra", out var extraEl))
        {
            if (extraEl.ValueKind != JsonValueKind.Object)
                throw new JsonException("checkpoint field 'extra' must be a JSON object");

            var builder = ImmutableDictionary.CreateBuilder<string, JsonElement>();
            foreach (var property in extraEl.EnumerateObject())
                builder[property.Name] = property.Value.Clone();
            extra = builder.ToImmutable();
        }

        return new SourceCheckpoint(lastUid, extra);
    }
}

/// <summary>
/// Routes plain System.Text.Json reads through <see cref="SourceCheckpoint.FromJson(JsonElement)"/>
/// so both sanctioned read paths behave identically (the storage default
/// <c>{}</c> yields an empty checkpoint, mistyped members throw), and writes
/// the canonical <c>{"last_uid":...,"extra":{...}}</c> shape (M2T1 review fix).
/// </summary>
internal sealed class SourceCheckpointConverter : JsonConverter<SourceCheckpoint>
{
    /// <inheritdoc />
    public override SourceCheckpoint Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return SourceCheckpoint.FromJson(doc.RootElement);
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer, SourceCheckpoint value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("last_uid");
        if (value.LastUid is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value.LastUid);

        writer.WritePropertyName("extra");
        writer.WriteStartObject();
        foreach (var pair in value.Extra)
        {
            writer.WritePropertyName(pair.Key);
            if (pair.Value.ValueKind == JsonValueKind.Undefined)
                writer.WriteNullValue(); // hand-built default(JsonElement) — keep the output valid
            else
                pair.Value.WriteTo(writer);
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
