using System.Collections.Concurrent;
using CodexBridge.Remote.Protocol;

namespace CodexBridge.Host.Remote;

public sealed class PriorityFrameWriter : IAsyncDisposable
{
    private readonly ConcurrentQueue<WriteRequest> _control = new();
    private readonly ConcurrentQueue<WriteRequest> _attachments = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<RemoteFrame, CancellationToken, Task> _sendAsync;
    private readonly Task _runner;

    public PriorityFrameWriter(Func<RemoteFrame, CancellationToken, Task> sendAsync)
    {
        _sendAsync = sendAsync ?? throw new ArgumentNullException(nameof(sendAsync));
        _runner = Task.Run(RunAsync);
    }

    public Task WriteControlAsync(RemoteFrame frame, CancellationToken cancellationToken = default) =>
        Enqueue(_control, frame, cancellationToken);

    public Task WriteAttachmentAsync(RemoteFrame frame, CancellationToken cancellationToken = default) =>
        Enqueue(_attachments, frame, cancellationToken);

    private Task Enqueue(
        ConcurrentQueue<WriteRequest> queue,
        RemoteFrame frame,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_lifetime.IsCancellationRequested) throw new ObjectDisposedException(nameof(PriorityFrameWriter));
        var request = new WriteRequest(frame, cancellationToken);
        queue.Enqueue(request);
        _available.Release();
        return request.Completion.Task;
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await _available.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                if (!_control.TryDequeue(out var request) && !_attachments.TryDequeue(out request))
                    continue;
                try
                {
                    request.CancellationToken.ThrowIfCancellationRequested();
                    await _sendAsync(request.Frame, request.CancellationToken).ConfigureAwait(false);
                    request.Completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    request.Completion.TrySetException(exception);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            while (_control.TryDequeue(out var control)) control.Completion.TrySetCanceled();
            while (_attachments.TryDequeue(out var attachment)) attachment.Completion.TrySetCanceled();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try { await _runner.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _lifetime.Dispose();
        _available.Dispose();
    }

    private sealed record WriteRequest(RemoteFrame Frame, CancellationToken CancellationToken)
    {
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
