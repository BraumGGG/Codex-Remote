using CodexBridge.Core;
using CodexBridge.Host.Auth;
using CodexBridge.Host.Services;
using CodexBridge.Windows;

namespace CodexBridge.Host.Tests;

public sealed class MessageSubmissionServiceTests : IDisposable
{
    private readonly string _directory = TestPaths.CreateDirectory("message-submit");

    [Fact]
    public async Task SubmitAsync_RejectsReadOnlyDeviceBeforeSender()
    {
        var sender = new RecordingSender();
        var service = CreateService(sender);

        var exception = await Assert.ThrowsAsync<MessageSubmissionException>(() =>
            service.SubmitAsync(
                new DevicePrincipal("free", "Free Phone", CanSend: false),
                AllowedCatalog.ThreadId,
                "不能发送",
                CancellationToken.None));

        Assert.Equal("forbidden_read_only", exception.ErrorCode);
        Assert.Equal(0, sender.CallCount);
    }

    [Theory]
    [InlineData("01a00748-fa69-7e13-8800-74eadbd62cf7", "hello", "thread_not_found")]
    [InlineData(AllowedCatalog.ThreadId, "", "invalid_message")]
    [InlineData(AllowedCatalog.ThreadId, "line1\nline2", "invalid_message")]
    public async Task SubmitAsync_RejectsUnsafeTargetOrTextBeforeSender(
        string threadId,
        string message,
        string errorCode)
    {
        var sender = new RecordingSender();
        var service = CreateService(sender);

        var exception = await Assert.ThrowsAsync<MessageSubmissionException>(() =>
            service.SubmitAsync(
                new DevicePrincipal("pro", "Pro Phone", CanSend: true),
                threadId,
                message,
                CancellationToken.None));

        Assert.Equal(errorCode, exception.ErrorCode);
        Assert.Equal(0, sender.CallCount);
    }

    [Fact]
    public async Task SubmitAsync_SendsAndAuditDoesNotContainMessageBody()
    {
        const string secretMessage = "SECRET-MESSAGE-MUST-NOT-BE-AUDITED";
        var sender = new RecordingSender();
        var service = CreateService(sender);

        var result = await service.SubmitAsync(
            new DevicePrincipal("pro", "Pro Phone", CanSend: true),
            AllowedCatalog.ThreadId,
            secretMessage,
            CancellationToken.None);

        Assert.Equal(AllowedCatalog.ThreadId, result.ThreadId);
        Assert.Equal(1, sender.CallCount);
        Assert.DoesNotContain(
            secretMessage,
            await File.ReadAllTextAsync(Path.Combine(_directory, "audit.jsonl")),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("desktop_unavailable")]
    [InlineData("interactive_session_unavailable")]
    [InlineData("desktop_version_unsupported")]
    public async Task SubmitAsync_MapsDesktopFailuresToStableCodes(string expectedCode)
    {
        const string secretMessage = "DESKTOP-FAILURE-SECRET";
        var sender = new ThrowingSender(expectedCode);
        var service = CreateService(sender);

        var exception = await Assert.ThrowsAsync<MessageSubmissionException>(() =>
            service.SubmitAsync(
                new DevicePrincipal("pro", "Pro Phone", CanSend: true),
                AllowedCatalog.ThreadId,
                secretMessage,
                CancellationToken.None));

        Assert.Equal(expectedCode, exception.ErrorCode);
        Assert.Equal(1, sender.CallCount);
        Assert.DoesNotContain(
            secretMessage,
            await File.ReadAllTextAsync(Path.Combine(_directory, "audit.jsonl")),
            StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private MessageSubmissionService CreateService(ICodexDesktopSender sender) => new(
        new AllowedCatalog(),
        sender,
        new DesktopCommandQueue(),
        new AuditLog(Path.Combine(_directory, "audit.jsonl"), TimeProvider.System),
        new TargetPolicy(),
        TimeProvider.System);

    private sealed class RecordingSender : ICodexDesktopSender
    {
        public int CallCount { get; private set; }

        public Task<SendResult> SendAsync(
            string projectPath,
            string threadId,
            string message,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new SendResult(threadId, "返回测试会话1", false));
        }
    }

    private sealed class ThrowingSender(string errorCode) : ICodexDesktopSender
    {
        public int CallCount { get; private set; }

        public Task<SendResult> SendAsync(
            string projectPath,
            string threadId,
            string message,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw errorCode switch
            {
                "desktop_unavailable" => new DesktopUnavailableException("offline"),
                "interactive_session_unavailable" =>
                    new InteractiveSessionUnavailableException("locked"),
                "desktop_version_unsupported" =>
                    new DesktopVersionUnsupportedException("ambiguous"),
                _ => new InvalidOperationException("unknown test error"),
            };
        }
    }

    private sealed class AllowedCatalog : IThreadCatalog
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
}
