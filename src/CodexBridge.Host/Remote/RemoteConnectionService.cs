using System.Text.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using CodexBridge.Host.Auth;
using CodexBridge.Remote.Protocol;

namespace CodexBridge.Host.Remote;

public interface IRemoteFrameTransport : IAsyncDisposable
{
    IAsyncEnumerable<RemoteFrame> ReadAllAsync(CancellationToken cancellationToken);
    Task SendAsync(RemoteFrame frame, CancellationToken cancellationToken);
}

public sealed class RemoteConnectionService(
    RemoteRpcDispatcher dispatcher,
    RemoteSubscriptionRegistry subscriptions,
    RemoteAttachmentStreamer? attachments = null,
    ILogger<RemoteConnectionService>? logger = null)
{
    public async Task RunAsync(
        DevicePrincipal principal,
        IRemoteFrameTransport transport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(transport);
        await using var writer = new PriorityFrameWriter(transport.SendAsync);
        var transfers = new ConcurrentDictionary<Guid, (CancellationTokenSource Cancellation, Task Runner)>();
        try
        {
            await foreach (var frame in transport.ReadAllAsync(cancellationToken))
            {
                if (frame.Kind != RemoteFrameKind.Request)
                {
                    await SendErrorAsync(writer, frame.RequestId, "invalid_frame_kind", cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                RpcRequest request;
                try
                {
                    request = RpcJson.DecodeRequest(frame.Payload.Span);
                }
                catch (RemoteProtocolException exception)
                {
                    await SendErrorAsync(writer, frame.RequestId, exception.ErrorCode, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (request.Method == RpcMethod.Subscribe)
                {
                    await HandleSubscribeAsync(writer, frame.RequestId, request.Parameters, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }
                if (request.Method == RpcMethod.Unsubscribe)
                {
                    await HandleUnsubscribeAsync(writer, frame.RequestId, request.Parameters, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }
                if (request.Method == RpcMethod.CancelTransfer)
                {
                    await HandleCancelTransferAsync(
                        writer, frame.RequestId, request.Parameters, transfers, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }
                if (attachments is not null && request.Method is RpcMethod.Image or RpcMethod.TextFile or RpcMethod.EventText)
                {
                    StartAttachmentTransfer(
                        writer,
                        frame.RequestId,
                        request,
                        transfers,
                        cancellationToken);
                    continue;
                }

                byte[] response;
                var rpcTimer = Stopwatch.StartNew();
                logger?.LogInformation("Remote RPC started: {Method}", request.Method);
                try
                {
                    response = await dispatcher.DispatchAsync(
                        principal,
                        frame.Payload,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var errorCode = ClassifyDispatchError(exception);
                    logger?.LogWarning(
                        "Remote RPC failed: {Method} {ErrorCode} after {ElapsedMs}ms",
                        request.Method, errorCode, rpcTimer.ElapsedMilliseconds);
                    await SendErrorAsync(writer, frame.RequestId, errorCode, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }
                logger?.LogInformation(
                    "Remote RPC completed: {Method} in {ElapsedMs}ms",
                    request.Method, rpcTimer.ElapsedMilliseconds);
                if (response.Length > RpcJson.MaximumResponsePayloadLength)
                {
                    logger?.LogWarning(
                        "Remote RPC response rejected: {Method} response_too_large {ResponseBytes} bytes after {ElapsedMs}ms",
                        request.Method, response.Length, rpcTimer.ElapsedMilliseconds);
                    await SendErrorAsync(
                        writer,
                        frame.RequestId,
                        "response_too_large",
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }
                await writer.WriteControlAsync(
                    new RemoteFrame(RemoteFrameKind.Response, frame.RequestId, 0, response),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var transfer in transfers.Values) transfer.Cancellation.Cancel();
            foreach (var transfer in transfers.Values)
            {
                try { await transfer.Runner.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                transfer.Cancellation.Dispose();
            }
            await subscriptions.DisposeAsync().ConfigureAwait(false);
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string ClassifyDispatchError(Exception exception) => exception switch
    {
        IOException => "remote_io_error",
        InvalidDataException => "remote_protocol_invalid",
        UnauthorizedAccessException => "remote_access_denied",
        TimeoutException => "remote_timeout",
        _ => "remote_request_failed",
    };

    private async Task HandleSubscribeAsync(
        PriorityFrameWriter writer,
        Guid requestId,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = RpcJson.DecodeParameters<SubscribeParameters>(parameters);
            await subscriptions.SubscribeAsync(
                request.ThreadId,
                requestId,
                request.AfterSequence,
                writer.WriteControlAsync,
                cancellationToken).ConfigureAwait(false);
            await SendSuccessAsync(writer, requestId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is RemoteProtocolException or KeyNotFoundException)
        {
            await SendErrorAsync(
                writer,
                requestId,
                exception is RemoteProtocolException protocol ? protocol.ErrorCode : "not_found",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleUnsubscribeAsync(
        PriorityFrameWriter writer,
        Guid requestId,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = RpcJson.DecodeParameters<UnsubscribeParameters>(parameters);
            await subscriptions.UnsubscribeAsync(request.ThreadId).ConfigureAwait(false);
            await SendSuccessAsync(writer, requestId, cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteProtocolException exception)
        {
            await SendErrorAsync(writer, requestId, exception.ErrorCode, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static Task SendSuccessAsync(
        PriorityFrameWriter writer,
        Guid requestId,
        CancellationToken cancellationToken) =>
        writer.WriteControlAsync(
            new RemoteFrame(
                RemoteFrameKind.Response,
                requestId,
                0,
                RpcJson.Encode(new RpcResponse(
                    true,
                    JsonSerializer.SerializeToElement(new { }, RpcJson.Options),
                    null))),
            cancellationToken);

    private static Task SendErrorAsync(
        PriorityFrameWriter writer,
        Guid requestId,
        string errorCode,
        CancellationToken cancellationToken) =>
        writer.WriteControlAsync(
            new RemoteFrame(
                RemoteFrameKind.Error,
                requestId,
                0,
                RpcJson.Encode(new RpcResponse(false, null, errorCode))),
            cancellationToken);

    private static async Task HandleCancelTransferAsync(
        PriorityFrameWriter writer,
        Guid requestId,
        JsonElement parameters,
        ConcurrentDictionary<Guid, (CancellationTokenSource Cancellation, Task Runner)> transfers,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = RpcJson.DecodeParameters<CancelTransferParameters>(parameters);
            if (transfers.TryRemove(request.TransferId, out var transfer))
            {
                transfer.Cancellation.Cancel();
                try { await transfer.Runner.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                transfer.Cancellation.Dispose();
            }
            await SendSuccessAsync(writer, requestId, cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteProtocolException exception)
        {
            await SendErrorAsync(writer, requestId, exception.ErrorCode, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private void StartAttachmentTransfer(
        PriorityFrameWriter writer,
        Guid transferId,
        RpcRequest request,
        ConcurrentDictionary<Guid, (CancellationTokenSource Cancellation, Task Runner)> transfers,
        CancellationToken connectionCancellation)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(connectionCancellation);
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = Task.Run(async () =>
        {
            await registered.Task.ConfigureAwait(false);
            try
            {
                if (request.Method == RpcMethod.Image)
                {
                    var parameters = RpcJson.DecodeParameters<AttachmentParameters>(request.Parameters);
                    await attachments!.StreamImageAsync(
                        parameters.ThreadId,
                        parameters.AttachmentId,
                        transferId,
                        writer,
                        cancellation.Token).ConfigureAwait(false);
                }
                else if (request.Method == RpcMethod.TextFile)
                {
                    var parameters = RpcJson.DecodeParameters<TextFileParameters>(request.Parameters);
                    await attachments!.StreamTextAsync(
                        parameters.ThreadId,
                        parameters.FileId,
                        transferId,
                        writer,
                        cancellation.Token).ConfigureAwait(false);
                }
                else
                {
                    var parameters = RpcJson.DecodeParameters<EventTextParameters>(request.Parameters);
                    await attachments!.StreamEventTextAsync(
                        parameters.ThreadId,
                        parameters.Sequence,
                        parameters.ContentId,
                        transferId,
                        writer,
                        cancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is
                RemoteProtocolException or KeyNotFoundException or InvalidDataException)
            {
                await SendErrorAsync(
                    writer,
                    transferId,
                    exception is RemoteProtocolException protocol ? protocol.ErrorCode : "attachment_not_found",
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                if (transfers.TryRemove(transferId, out var completed)) completed.Cancellation.Dispose();
            }
        }, CancellationToken.None);
        if (!transfers.TryAdd(transferId, (cancellation, runner)))
        {
            cancellation.Cancel();
            registered.TrySetCanceled();
            cancellation.Dispose();
            throw new InvalidOperationException("Transfer ID is already active.");
        }
        registered.SetResult();
    }
}
