using CodexBridge.Core;

namespace CodexBridge.Windows;

public sealed record RolloutIndexEntry(
    long Sequence,
    long Offset,
    int Length,
    ConversationEventKind Kind,
    DateTimeOffset? Timestamp);

public sealed record RolloutIndexSummary(
    string Identity,
    long SourceLength,
    long IndexedLength,
    long LatestSequence,
    string Status,
    IReadOnlyList<RolloutIndexEntry> Entries);

public interface IRolloutIndexStore
{
    Task<RolloutIndexSummary> GetSummaryAsync(string rolloutPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<(long Sequence, ConversationEvent Event)>> GetPageAsync(
        string rolloutPath,
        long beforeSequence,
        int pageSize,
        CancellationToken cancellationToken = default);
    Task<ConversationEvent?> GetEventAsync(
        string rolloutPath,
        long sequence,
        CancellationToken cancellationToken = default);
}
