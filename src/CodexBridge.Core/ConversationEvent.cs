namespace CodexBridge.Core;

public enum ConversationEventKind
{
    UserMessage,
    AgentMessage,
    TaskStarted,
    TaskCompleted,
    TokenCount,
    Unknown,
}

public sealed record ConversationEvent(
    ConversationEventKind Kind,
    DateTimeOffset? Timestamp,
    string? Text,
    string? TurnId,
    string RawType,
    IReadOnlyList<string>? LocalImagePaths = null);
