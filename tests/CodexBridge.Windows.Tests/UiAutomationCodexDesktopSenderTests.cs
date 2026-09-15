using CodexBridge.Core;

namespace CodexBridge.Windows.Tests;

public sealed class UiAutomationCodexDesktopSenderTests
{
    private const string Project = @"D:\authorized\dynamic-project";
    private const string ThreadId = "01a00749-6d7c-7072-9b22-f4a70ea35331";

    [Fact]
    public async Task SendAsync_QueuesStableUuidRegardlessOfTitleOrDisplayName()
    {
        var operations = new List<string>();
        var activator = new RecordingThreadActivator(operations);
        var queue = new RecordingQueueClient { Operations = operations };
        var sender = CreateSender(new ThreadSummary(
            ThreadId, "很长的第一条用户输入", Project, "", 1, false, "rollout", "custom", "Desktop 改名后的会话"), activator, queue);

        var result = await sender.SendAsync(Project, ThreadId, "你现在是什么模型");

        Assert.Equal(ThreadId, activator.ThreadId);
        Assert.Equal(ThreadId, queue.ThreadId);
        Assert.Equal("你现在是什么模型", queue.Message);
        Assert.Equal("Desktop 改名后的会话", result.ThreadTitle);
        Assert.Equal(["activate", "queue"], operations);
    }

    [Fact]
    public async Task SendAsync_AllowsEmptyOrDuplicateTitlesBecauseTheyAreNotIdentifiers()
    {
        var activator = new RecordingThreadActivator();
        var queue = new RecordingQueueClient();
        var sender = CreateSender(new ThreadSummary(ThreadId, "", Project, "", 1, false, "rollout", "custom", "重复名称"), activator, queue);

        await sender.SendAsync(Project, ThreadId, "hello");

        Assert.Equal(ThreadId, activator.ThreadId);
        Assert.Equal(ThreadId, queue.ThreadId);
        Assert.Equal(1, queue.CallCount);
    }

    [Fact]
    public async Task SendAsync_RejectsMismatchedProjectBeforeQueueing()
    {
        var activator = new RecordingThreadActivator();
        var queue = new RecordingQueueClient();
        var sender = CreateSender(new ThreadSummary(ThreadId, "title", Project, "", 1, false, "rollout", "custom"), activator, queue, [Project, @"D:\authorized\other-project"]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sender.SendAsync(@"D:\authorized\other-project", ThreadId, "hello"));
        Assert.Equal(0, activator.CallCount);
        Assert.Equal(0, queue.CallCount);
    }

    [Fact]
    public async Task SendAsync_DoesNotQueueWhenActivationFails()
    {
        var activator = new RecordingThreadActivator
        {
            Exception = new DesktopUnavailableException("activation failed"),
        };
        var queue = new RecordingQueueClient();
        var sender = CreateSender(new ThreadSummary(ThreadId, "title", Project, "", 1, false, "rollout", "custom"), activator, queue);

        await Assert.ThrowsAsync<DesktopUnavailableException>(() => sender.SendAsync(Project, ThreadId, "hello"));

        Assert.Equal(1, activator.CallCount);
        Assert.Equal(0, queue.CallCount);
    }

    [Fact]
    public async Task SendAsync_DoesNotRetryFailedQueue()
    {
        var activator = new RecordingThreadActivator();
        var queue = new RecordingQueueClient { Exception = new DesktopVersionUnsupportedException("queue failed") };
        var sender = CreateSender(new ThreadSummary(ThreadId, "title", Project, "", 1, false, "rollout", "custom"), activator, queue);

        await Assert.ThrowsAsync<DesktopVersionUnsupportedException>(() => sender.SendAsync(Project, ThreadId, "hello"));
        Assert.Equal(1, activator.CallCount);
        Assert.Equal(1, queue.CallCount);
    }

    private static UiAutomationCodexDesktopSender CreateSender(
        ThreadSummary thread,
        RecordingThreadActivator activator,
        RecordingQueueClient queue,
        IReadOnlyList<string>? projects = null) =>
        new(new FakeCatalog(thread), new TargetPolicy(projects ?? [Project]), activator, queue);

    private sealed class RecordingThreadActivator(List<string>? operations = null) : ICodexThreadActivator
    {
        public int CallCount { get; private set; }
        public string? ThreadId { get; private set; }
        public Exception? Exception { get; init; }

        public Task ActivateAsync(string threadId, CancellationToken cancellationToken)
        {
            CallCount++;
            ThreadId = threadId;
            operations?.Add("activate");
            return Exception is null ? Task.CompletedTask : Task.FromException(Exception);
        }
    }

    private sealed class RecordingQueueClient : ICodexQueueClient
    {
        public int CallCount { get; private set; }
        public string? ThreadId { get; private set; }
        public string? Message { get; private set; }
        public Exception? Exception { get; init; }

        public Task QueueAsync(string threadId, string message, CancellationToken cancellationToken)
        {
            CallCount++; ThreadId = threadId; Message = message;
            Operations?.Add("queue");
            return Exception is null ? Task.CompletedTask : Task.FromException(Exception);
        }

        public List<string>? Operations { get; init; }
    }

    private sealed class FakeCatalog(ThreadSummary thread) : IThreadCatalog
    {
        public Task<IReadOnlyList<ThreadSummary>> ListByProjectAsync(string projectPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ThreadSummary?> GetAsync(string threadId, CancellationToken cancellationToken = default) => Task.FromResult<ThreadSummary?>(threadId == thread.Id ? thread : null);
    }
}
