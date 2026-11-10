namespace EmailAgent.Infrastructure.Actions;

/// <summary>Thrown when the dispatch request references a missing agent config (caller maps it).</summary>
public sealed class AgentConfigNotFoundException(Guid agentConfigId, Guid userId)
    : Exception($"Agent config '{agentConfigId}' was not found for user '{userId}'.");

/// <summary>Thrown when the dispatch request references a missing processed message (precondition, §10/Q-O9).</summary>
public sealed class ProcessedMessageNotFoundException(Guid processedMessageId)
    : Exception($"Processed message '{processedMessageId}' was not found.");
