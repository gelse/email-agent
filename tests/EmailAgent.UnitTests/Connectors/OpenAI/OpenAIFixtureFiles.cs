using System.Text.Json;
using EmailAgent.Contracts.AgentEngines;
using EmailAgent.Contracts.MessageSources;
using EmailAgent.Connectors.OpenAI;
using Json.Schema;

namespace EmailAgent.UnitTests.Connectors.OpenAI;

/// <summary>
/// Shared M3T1 test data: the committed fixture directory under
/// <c>tests/fixtures/openai</c> (AGENTS.md hard constraint #5 — recorded
/// fixtures only, never a real provider), a minimal valid engine config and an
/// <see cref="AgentRequest"/> factory for the request-builder / engine tests.
/// </summary>
internal static class OpenAIFixtureFiles
{
    /// <summary>The recorded fixture directory, located by walking up from the test output.</summary>
    internal static string DirectoryPath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, "tests", "fixtures", "openai");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException(
                "tests/fixtures/openai could not be located from " + AppContext.BaseDirectory);
        }
    }

    /// <summary>Reads one committed fixture file as text.</summary>
    internal static string Read(string fileName) =>
        File.ReadAllText(Path.Combine(DirectoryPath, fileName));

    /// <summary>
    /// A minimal schema-valid engine config — the recorded test key marker
    /// only (<c>sk-test-redacted</c>), never a real credential (AGENTS.md #5).
    /// </summary>
    internal static JsonElement HappyConfig()
    {
        using JsonDocument doc = JsonDocument.Parse(
            """
            {
              "base_url": "https://llm.example.test/v1",
              "api_key": "sk-test-redacted",
              "model_name": "gpt-4o-mini"
            }
            """);
        return doc.RootElement.Clone();
    }

    /// <summary>An <see cref="AgentRequest"/> with the SQ-M3-9 prompt convention and the test config attached.</summary>
    internal static AgentRequest Request(
        string prompt = "SYSTEM:\nYou are terse.\n\nUSER:\nSay hello world",
        IReadOnlyDictionary<string, JsonSchema>? allowedActions = null,
        JsonElement? config = null) =>
        new(
            Message: new RemoteMessage(
                "uid-1", DateTimeOffset.UnixEpoch, new Dictionary<string, string>(), "body", []),
            Prompt: prompt,
            AllowedActionSchemas: allowedActions ?? new Dictionary<string, JsonSchema>(),
            Context: new AgentContext(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)
            {
                EngineConfigJson = config ?? HappyConfig(),
            });
}
