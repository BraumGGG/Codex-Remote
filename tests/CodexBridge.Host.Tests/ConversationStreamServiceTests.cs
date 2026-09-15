using CodexBridge.Core;
using CodexBridge.Host.Services;

namespace CodexBridge.Host.Tests;

public sealed class ConversationStreamServiceTests
{
    [Fact]
    public async Task StreamAsync_SkipsHiddenAndEventsAtOrBeforeCursor()
    {
        var service = new ConversationStreamService(
            new OneThreadCatalog(),
            new FixedReader(),
            new TargetPolicy());
        var received = new List<long>();

        await foreach (var item in service.StreamAsync(
                           OneThreadCatalog.ThreadId,
                           afterSequence: 1,
                           CancellationToken.None))
        {
            received.Add(item.Sequence);
        }

        Assert.Equal([2L, 3L], received);
    }

    [Fact]
    public async Task StreamAsync_RejectsThirdThreadBeforeOpeningReader()
    {
        var reader = new FixedReader();
        var service = new ConversationStreamService(
            new OneThreadCatalog(),
            reader,
            new TargetPolicy());

        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
        {
            await foreach (var _ in service.StreamAsync(
                               "01a00748-fa69-7e13-8800-74eadbd62cf7",
                               0,
                               CancellationToken.None))
            {
            }
        });
        Assert.False(reader.WasOpened);
    }

    private sealed class OneThreadCatalog : IThreadCatalog
    {
        public const string ThreadId = "01a00749-2fb0-7fa0-9186-4f8292732f2c";

        public Task<IReadOnlyList<ThreadSummary>> ListByProjectAsync(
            string projectPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ThreadSummary?> GetAsync(
            string threadId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ThreadSummary?>(threadId == ThreadId
                ? new ThreadSummary(
                    ThreadId,
                    "返回测试会话1",
                    TargetPolicy.AllowedProjectPath,
                    string.Empty,
                    1,
                    false,
                    "rollout.jsonl",
                    "custom")
                : null);
    }

    private sealed class FixedReader : IConversationReader
    {
        public bool WasOpened { get; private set; }

        public async IAsyncEnumerable<ConversationEvent> ReadAsync(
            string rolloutPath,
            bool follow,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            WasOpened = true;
            yield return Event(ConversationEventKind.UserMessage);
            yield return Event(ConversationEventKind.Unknown);
            yield return Event(ConversationEventKind.AgentMessage);
            yield return Event(ConversationEventKind.TaskCompleted);
            await Task.CompletedTask;
        }

        private static ConversationEvent Event(ConversationEventKind kind) =>
            new(kind, DateTimeOffset.UtcNow, kind.ToString(), null, kind.ToString());
    }
}
