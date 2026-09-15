using CodexBridge.Core;
using CodexBridge.Host.Auth;
using CodexBridge.Host.Remote;
using CodexBridge.Host.Services;
using CodexBridge.Windows;

namespace CodexBridge.Host.Tests;

public sealed class RemoteMessageSubmissionServiceTests : IDisposable
{
    private readonly string _directory = TestPaths.CreateDirectory("remote-submit");

    [Fact]
    public async Task ConcurrentDuplicateCommand_CallsSenderExactlyOnce()
    {
        var sender = new BlockingSender();
        var service = CreateService(sender);
        var commandId = Guid.NewGuid();
        var principal = new DevicePrincipal("pro", "phone", true);
        var first = service.SubmitOnceAsync(principal, commandId, AllowedCatalog.FirstThread, "secret body");
        await sender.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var duplicate = await service.SubmitOnceAsync(
            principal, commandId, AllowedCatalog.FirstThread, "secret body");
        sender.Release.SetResult();
        var completed = await first;

        Assert.Equal(RemoteCommandState.Accepted, duplicate.State);
        Assert.Equal(RemoteCommandState.Completed, completed.State);
        Assert.Equal(1, sender.CallCount);
        Assert.DoesNotContain(
            "secret body",
            await File.ReadAllTextAsync(Path.Combine(_directory, "receipts.json")),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "01a00749-2fb0-7fa0-9186-4f8292732f2c", "forbidden_read_only")]
    [InlineData(true, "01a00748-fa69-7e13-8800-74eadbd62cf7", "thread_not_found")]
    public async Task InvalidAuthorizationOrThread_RejectsBeforeReceipt(
        bool canSend,
        string threadId,
        string errorCode)
    {
        var sender = new BlockingSender(releaseImmediately: true);
        var service = CreateService(sender);
        var exception = await Assert.ThrowsAsync<MessageSubmissionException>(() => service.SubmitOnceAsync(
            new DevicePrincipal("device", "phone", canSend),
            Guid.NewGuid(),
            threadId,
            "text"));

        Assert.Equal(errorCode, exception.ErrorCode);
        Assert.Equal(0, sender.CallCount);
        Assert.False(File.Exists(Path.Combine(_directory, "receipts.json")));
    }

    [Fact]
    public async Task DuplicateCommandWithChangedText_IsRejectedWithoutSecondSenderCall()
    {
        var sender = new BlockingSender(releaseImmediately: true);
        var service = CreateService(sender);
        var commandId = Guid.NewGuid();
        var principal = new DevicePrincipal("pro", "phone", true);
        await service.SubmitOnceAsync(principal, commandId, AllowedCatalog.FirstThread, "first secret");

        var conflict = await service.SubmitOnceAsync(
            principal, commandId, AllowedCatalog.FirstThread, "changed secret");

        Assert.Equal(RemoteCommandState.Rejected, conflict.State);
        Assert.Equal("command_id_conflict", conflict.ErrorCode);
        Assert.Equal(1, sender.CallCount);
    }

    [Fact]
    public async Task CancellationAfterAccepted_MarksUnknownAndNeverCallsSenderAgain()
    {
        var sender = new BlockingSender();
        var service = CreateService(sender);
        var commandId = Guid.NewGuid();
        var principal = new DevicePrincipal("pro", "phone", true);
        using var cancellation = new CancellationTokenSource();
        var first = service.SubmitOnceAsync(
            principal, commandId, AllowedCatalog.FirstThread, "possibly sent", cancellation.Token);
        await sender.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var duplicate = await service.SubmitOnceAsync(
            principal, commandId, AllowedCatalog.FirstThread, "possibly sent");

        Assert.Equal(RemoteCommandState.Unknown, duplicate.State);
        Assert.Equal("send_result_unknown", duplicate.ErrorCode);
        Assert.Equal(1, sender.CallCount);
    }

    [Fact]
    public async Task DifferentThreads_SerializeOnlyDesktopInputActions()
    {
        var sender = new OrderedSender();
        var service = CreateService(sender);
        var principal = new DevicePrincipal("pro", "phone", true);
        var first = service.SubmitOnceAsync(
            principal, Guid.NewGuid(), AllowedCatalog.FirstThread, "first");
        await sender.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.SubmitOnceAsync(
            principal, Guid.NewGuid(), AllowedCatalog.SecondThread, "second");

        await Task.Delay(100);
        Assert.False(sender.SecondEntered.Task.IsCompleted);
        sender.ReleaseFirst.TrySetResult();
        await sender.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        sender.ReleaseSecond.TrySetResult();

        Assert.Equal(RemoteCommandState.Completed, (await first).State);
        Assert.Equal(RemoteCommandState.Completed, (await second).State);
        Assert.Equal(
            [AllowedCatalog.FirstThread, AllowedCatalog.SecondThread],
            sender.ThreadIds);
    }

    private RemoteMessageSubmissionService CreateService(ICodexDesktopSender sender)
    {
        var submissions = new MessageSubmissionService(
            new AllowedCatalog(),
            sender,
            new DesktopCommandQueue(),
            new AuditLog(Path.Combine(_directory, "audit.jsonl"), TimeProvider.System),
            new TargetPolicy(),
            TimeProvider.System);
        return new RemoteMessageSubmissionService(
            new RemoteCommandReceiptStore(Path.Combine(_directory, "receipts.json"), TimeProvider.System),
            submissions);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class BlockingSender : ICodexDesktopSender
    {
        public BlockingSender(bool releaseImmediately = false)
        {
            if (releaseImmediately) Release.TrySetResult();
        }
        public int CallCount { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<SendResult> SendAsync(
            string projectPath,
            string threadId,
            string message,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new SendResult(threadId, "thread", false);
        }
    }

    private sealed class OrderedSender : ICodexDesktopSender
    {
        private int _calls;
        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> ThreadIds { get; } = [];

        public async Task<SendResult> SendAsync(
            string projectPath,
            string threadId,
            string message,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            ThreadIds.Add(threadId);
            if (call == 1)
            {
                FirstEntered.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
            }
            else
            {
                SecondEntered.TrySetResult();
                await ReleaseSecond.Task.WaitAsync(cancellationToken);
            }
            return new SendResult(threadId, "thread", false);
        }
    }

    private sealed class AllowedCatalog : IThreadCatalog
    {
        public const string FirstThread = "01a00749-2fb0-7fa0-9186-4f8292732f2c";
        public const string SecondThread = "01a00749-6d7c-7072-9b22-f4a70ea35331";
        private static readonly ThreadSummary[] Threads = RemoteSubscriptionRegistryTests.AllowedThreadIds
            .Select(id => new ThreadSummary(
                id, "thread", TargetPolicy.AllowedProjectPath, "", 1, false, "rollout", "test"))
            .ToArray();
        public Task<IReadOnlyList<ThreadSummary>> ListByProjectAsync(string projectPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ThreadSummary>>(Threads);
        public Task<ThreadSummary?> GetAsync(string threadId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Threads.FirstOrDefault(thread => thread.Id == threadId));
    }
}
