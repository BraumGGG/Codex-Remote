using System.Diagnostics;
using CodexBridge.Core;
using CodexBridge.Host.Auth;
using CodexBridge.Host.Contracts;
using CodexBridge.Windows;

namespace CodexBridge.Host.Services;

public sealed class MessageSubmissionException : Exception
{
    public MessageSubmissionException(string errorCode, Exception? innerException = null)
        : base(errorCode, innerException)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

public sealed class MessageSubmissionService
{
    private readonly IThreadCatalog _catalog;
    private readonly ICodexDesktopSender _sender;
    private readonly DesktopCommandQueue _queue;
    private readonly AuditLog _audit;
    private readonly TargetPolicy _policy;
    private readonly TimeProvider _timeProvider;

    public MessageSubmissionService(
        IThreadCatalog catalog,
        ICodexDesktopSender sender,
        DesktopCommandQueue queue,
        AuditLog audit,
        TargetPolicy policy,
        TimeProvider timeProvider)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<MessageAcceptedResponse> SubmitAsync(
        DevicePrincipal principal,
        string threadId,
        string? message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var thread = await ValidateAsync(principal, threadId, message, cancellationToken).ConfigureAwait(false);
            await _queue.EnqueueAsync(
                token => _sender.SendAsync(
                    thread.Cwd,
                    threadId,
                    message!,
                    token),
                cancellationToken);

            var acceptedAt = _timeProvider.GetUtcNow();
            await WriteAuditAsync(
                principal,
                threadId,
                "accepted",
                errorCode: null,
                stopwatch.ElapsedMilliseconds,
                cancellationToken);
            return new MessageAcceptedResponse(
                Guid.NewGuid().ToString("N"),
                threadId,
                acceptedAt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var mapped = MapException(exception);
            await WriteAuditAsync(
                principal,
                threadId,
                "rejected",
                mapped.ErrorCode,
                stopwatch.ElapsedMilliseconds,
                CancellationToken.None);
            throw mapped;
        }
    }

    public async Task<ThreadSummary> ValidateAsync(
        DevicePrincipal principal,
        string threadId,
        string? message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ValidateRequest(principal, threadId, message);
        var thread = await _catalog.GetAsync(threadId, cancellationToken).ConfigureAwait(false);
        if (thread is null) throw new MessageSubmissionException("thread_not_found");
        try { _policy.AssertCanSend(thread.Cwd, thread.Id); }
        catch (UnauthorizedAccessException exception) when (exception.Message == "Project is read-only.")
        { throw new MessageSubmissionException("forbidden_project_read_only", exception); }
        return thread;
    }

    private void ValidateRequest(DevicePrincipal principal, string threadId, string? message)
    {
        if (!principal.CanSend)
        {
            throw new MessageSubmissionException("forbidden_read_only");
        }

        if (string.IsNullOrWhiteSpace(threadId)) throw new MessageSubmissionException("thread_not_found");

        if (string.IsNullOrWhiteSpace(message) ||
            message.Length > 4_000 ||
            message.Contains('\r') ||
            message.Contains('\n'))
        {
            throw new MessageSubmissionException("invalid_message");
        }
    }

    private static MessageSubmissionException MapException(Exception exception) => exception switch
    {
        MessageSubmissionException submission => submission,
        UnauthorizedAccessException => new MessageSubmissionException("thread_not_found", exception),
        DesktopUnavailableException => new MessageSubmissionException("desktop_unavailable", exception),
        InteractiveSessionUnavailableException =>
            new MessageSubmissionException("interactive_session_unavailable", exception),
        DesktopVersionUnsupportedException =>
            new MessageSubmissionException("desktop_version_unsupported", exception),
        _ => new MessageSubmissionException("send_failed", exception),
    };

    private Task WriteAuditAsync(
        DevicePrincipal principal,
        string threadId,
        string result,
        string? errorCode,
        long durationMs,
        CancellationToken cancellationToken) =>
        _audit.WriteAsync(
            new AuditEvent(
                _audit.GetUtcNow(),
                principal.DeviceId,
                "send",
                threadId,
                result,
                errorCode,
                durationMs),
            cancellationToken);
}
