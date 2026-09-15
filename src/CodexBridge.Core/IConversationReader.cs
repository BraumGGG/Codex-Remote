namespace CodexBridge.Core;

public interface IConversationReader
{
    IAsyncEnumerable<ConversationEvent> ReadAsync(
        string rolloutPath,
        bool follow,
        CancellationToken cancellationToken = default);
}
