using System.Text.Json;
using EmailAgent.Contracts.MessageSources;
using FluentAssertions;
using Xunit;

namespace EmailAgent.UnitTests.Contracts;

/// <summary>
/// M2T1 step 6/15: <see cref="SourceCheckpoint"/> canonical JSON
/// (<c>{"last_uid":...,"extra":{...}}</c>), the <c>Empty</c> round-trip and the
/// lenient <see cref="SourceCheckpoint.FromJson(JsonElement)"/> parser
/// (§6 <c>checkpoint_json jsonb DEFAULT '{}'</c>).
/// </summary>
public class SourceCheckpointTests
{
    [Fact]
    public void SourceCheckpoint_Empty_RoundTripsJson()
    {
        var json = JsonSerializer.Serialize(SourceCheckpoint.Empty);

        json.Should().Be("{\"last_uid\":null,\"extra\":{}}");

        var back = JsonSerializer.Deserialize<SourceCheckpoint>(json);

        back.Should().NotBeNull();
        back!.LastUid.Should().BeNull();
        back.Extra.Should().BeEmpty();
    }

    [Fact]
    public void SourceCheckpoint_WithLastUidAndExtra_RoundTripsJson()
    {
        using var extraDoc = JsonDocument.Parse("{\"folder\":\"INBOX\",\"seq\":42}");
        var checkpoint = new SourceCheckpoint(
            "12345",
            new Dictionary<string, JsonElement>
            {
                ["folder"] = extraDoc.RootElement.GetProperty("folder").Clone(),
                ["seq"] = extraDoc.RootElement.GetProperty("seq").Clone(),
            });

        var json = JsonSerializer.Serialize(checkpoint);

        json.Should().Contain("\"last_uid\":\"12345\"");
        json.Should().Contain("\"folder\":\"INBOX\"");
        json.Should().Contain("\"seq\":42");

        var back = JsonSerializer.Deserialize<SourceCheckpoint>(json);

        back.Should().NotBeNull();
        back!.LastUid.Should().Be("12345");
        back.Extra.Should().HaveCount(2);
        back.Extra["folder"].GetString().Should().Be("INBOX");
        back.Extra["seq"].GetInt32().Should().Be(42);
    }

    [Fact]
    public void SourceCheckpoint_FromJson_ToleratesDbDefault_ClonesValues_And_ThrowsOnInvalidShape()
    {
        // The DB default `{}` must parse: missing last_uid -> null, missing extra -> empty.
        using (var dbDefault = JsonDocument.Parse("{}"))
        {
            var lenient = SourceCheckpoint.FromJson(dbDefault.RootElement);
            lenient.LastUid.Should().BeNull();
            lenient.Extra.Should().BeEmpty();
        }

        // Cloned values must survive disposal of the source JsonDocument
        // (SourceCheckpoint XML-doc contract).
        var source = JsonDocument.Parse("{\"last_uid\":\"9\",\"extra\":{\"k\":\"v\"}}");
        var checkpoint = SourceCheckpoint.FromJson(source.RootElement);
        source.Dispose();
        checkpoint.LastUid.Should().Be("9");
        checkpoint.Extra["k"].GetString().Should().Be("v");

        // Present-but-mistyped members are rejected.
        using (var invalid = JsonDocument.Parse("[1,2]"))
        {
            var act = () => SourceCheckpoint.FromJson(invalid.RootElement);
            act.Should().Throw<JsonException>();
        }
    }

    [Fact]
    public void SourceCheckpoint_PlainJsonSerializer_SharesTheFromJsonContract()
    {
        // The storage default {} must yield an empty, non-null checkpoint via
        // the plain serializer too (the converter routes reads through FromJson).
        var dbDefault = JsonSerializer.Deserialize<SourceCheckpoint>("{}");

        dbDefault.Should().NotBeNull();
        dbDefault!.LastUid.Should().BeNull();
        dbDefault.Extra.Should().NotBeNull();
        dbDefault.Extra.Should().BeEmpty();

        // Mistyped members fail loudly on both paths.
        var mistypedUid = () => JsonSerializer.Deserialize<SourceCheckpoint>("{\"last_uid\":5}");
        mistypedUid.Should().Throw<JsonException>();

        var nullExtra = () => JsonSerializer.Deserialize<SourceCheckpoint>("{\"extra\":null}");
        nullExtra.Should().Throw<JsonException>();
    }

    [Fact]
    public void SourceCheckpoint_FromJson_MistypedMembers_Throw()
    {
        using (var badUid = JsonDocument.Parse("{\"last_uid\":5}"))
        {
            var act = () => SourceCheckpoint.FromJson(badUid.RootElement);
            act.Should().Throw<JsonException>();
        }

        using (var badExtra = JsonDocument.Parse("{\"extra\":[]}"))
        {
            var act = () => SourceCheckpoint.FromJson(badExtra.RootElement);
            act.Should().Throw<JsonException>();
        }
    }
}
