using System.Runtime.CompilerServices;
using System.Text.Json;
using EmailAgent.Contracts;
using EmailAgent.Contracts.MessageSources;
using FluentAssertions;
using Json.Schema;
using Xunit;

namespace EmailAgent.UnitTests.Contracts;

/// <summary>
/// M2T1 step 15: a minimal in-test <see cref="IMessageSource"/> stub proving
/// the interface (including the <see cref="JsonSchema.ConfigSchema"/> property)
/// compiles and can be consumed — M2T1 ships no implementation (Out of scope).
/// </summary>
public class IMessageSourceTests
{
    [Fact]
    public async Task IMessageSource_Default_ConfigSchema_Is_Non_Null_For_Test_Stub()
    {
        var source = new TestMessageSource();

        source.TypeId.Should().Be("test");
        source.ConfigSchema.Should().NotBeNull();
        source.ConfigSchema.BoolValue.Should().BeTrue(); // the stub returns JsonSchema.True

        var validation = await source.ValidateConfigAsync(default, CancellationToken.None);
        validation.Ok.Should().BeTrue();

        var checkpoint = await source.AdvanceCheckpointAsync(
            default, SourceCheckpoint.Empty, [], CancellationToken.None);
        checkpoint.LastUid.Should().BeNull();
        checkpoint.Extra.Should().BeEmpty();

        await using var messages = source.ListNewMessagesAsync(
            default, SourceCheckpoint.Empty, CancellationToken.None).GetAsyncEnumerator();
        (await messages.MoveNextAsync()).Should().BeFalse(); // the stub yields no messages
    }

    /// <summary>Compile-time probe only — intentionally returns nothing.</summary>
    private sealed class TestMessageSource : IMessageSource
    {
        public string TypeId => "test";

        public JsonSchema ConfigSchema => JsonSchema.True;

        public Task<ValidationResult> ValidateConfigAsync(
            JsonElement config, CancellationToken ct) =>
            Task.FromResult(ValidationResult.Success());

        public async IAsyncEnumerable<RemoteMessage> ListNewMessagesAsync(
            JsonElement config,
            SourceCheckpoint checkpoint,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield break;
        }

        public Task<SourceCheckpoint> AdvanceCheckpointAsync(
            JsonElement config,
            SourceCheckpoint checkpoint,
            IReadOnlyList<RemoteMessage> processed,
            CancellationToken ct) => Task.FromResult(checkpoint);
    }
}
