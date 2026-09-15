using System.Text.Json;
using CodexBridge.Host.Auth;
using CodexBridge.Host.Services;
using CodexBridge.Remote.Protocol;

namespace CodexBridge.Host.Remote;

public sealed class RemoteRpcDispatcher
{
    private readonly WorkspaceQueryService _workspace;
    private readonly ImageAttachmentService _images;
    private readonly TextFileAttachmentService _files;
    private readonly DesktopStatusService _desktop;
    private readonly RemoteMessageSubmissionService? _remoteSubmissions;
    private readonly IRemoteCapabilityResolver? _capabilityResolver;

    public RemoteRpcDispatcher(
        WorkspaceQueryService workspace,
        ImageAttachmentService images,
        TextFileAttachmentService files,
        DesktopStatusService desktop,
        RemoteMessageSubmissionService? remoteSubmissions = null,
        IRemoteCapabilityResolver? capabilityResolver = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _images = images ?? throw new ArgumentNullException(nameof(images));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
        _remoteSubmissions = remoteSubmissions;
        _capabilityResolver = capabilityResolver;
    }

    public async Task<byte[]> DispatchAsync(
        DevicePrincipal principal,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        principal = _capabilityResolver?.Resolve(principal) ?? principal;
        try
        {
            var request = RpcJson.DecodeRequest(payload.Span);
            object result = request.Method switch
            {
                RpcMethod.Status => DispatchStatus(request.Parameters),
                RpcMethod.Capabilities => DispatchCapabilities(request.Parameters, principal),
                RpcMethod.Projects => await DispatchProjectsAsync(request.Parameters, cancellationToken),
                RpcMethod.Threads => await DispatchThreadsAsync(request.Parameters, cancellationToken),
                RpcMethod.Events => await DispatchEventsAsync(request.Parameters, cancellationToken),
                RpcMethod.Image => await DispatchImageAsync(request.Parameters, cancellationToken),
                RpcMethod.TextFile => await DispatchTextFileAsync(request.Parameters, cancellationToken),
                RpcMethod.EventText => throw new RemoteRpcException("transfer_requires_connection"),
                RpcMethod.Subscribe or RpcMethod.Unsubscribe =>
                    throw new RemoteRpcException("subscription_requires_connection"),
                RpcMethod.SubmitText when !principal.CanSend =>
                    throw new RemoteRpcException("forbidden_read_only"),
                RpcMethod.SubmitText => await DispatchSubmissionAsync(
                    request.Parameters, principal, cancellationToken),
                RpcMethod.CancelTransfer => ValidatePendingCancellation(request.Parameters),
                _ => throw new RemoteRpcException("unknown_rpc_method"),
            };
            return RpcJson.Encode(new RpcResponse(
                Success: true,
                JsonSerializer.SerializeToElement(result, RpcJson.Options),
                ErrorCode: null));
        }
        catch (Exception exception) when (exception is
            RemoteProtocolException or RemoteRpcException or MessageSubmissionException or
            KeyNotFoundException or ArgumentOutOfRangeException)
        {
            var errorCode = exception switch
            {
                RemoteProtocolException protocol => protocol.ErrorCode,
                RemoteRpcException rpc => rpc.ErrorCode,
                MessageSubmissionException submission => submission.ErrorCode,
                KeyNotFoundException => "not_found",
                ArgumentOutOfRangeException => "invalid_parameters",
                _ => "request_rejected",
            };
            return RpcJson.Encode(new RpcResponse(false, null, errorCode));
        }
    }

    private object DispatchStatus(JsonElement parameters)
    {
        RpcJson.DecodeParameters<EmptyParameters>(parameters);
        return new
        {
            hostVersion = typeof(RemoteRpcDispatcher).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            desktopOnline = _desktop.IsOnline(),
        };
    }

    private object DispatchCapabilities(JsonElement parameters, DevicePrincipal principal)
    {
        RpcJson.DecodeParameters<EmptyParameters>(parameters);
        var entitlement = _capabilityResolver?.Evaluate(principal.DeviceId);
        return new
        {
            canView = true,
            canSend = principal.CanSend,
            plan = principal.CanSend ? "pro" : "free",
            entitlementState = entitlement?.State.ToString().ToLowerInvariant() ?? (principal.CanSend ? "pro" : "free"),
            entitlement?.ExpiresAt,
            entitlement?.OfflineUntil,
        };
    }

    private async Task<object> DispatchProjectsAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        RpcJson.DecodeParameters<EmptyParameters>(parameters);
        return await _workspace.GetProjectsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> DispatchThreadsAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var request = RpcJson.DecodeParameters<ThreadsParameters>(parameters);
        return await _workspace.GetThreadsAsync(
            request.ProjectId,
            request.Cursor,
            request.PageSize,
            request.Query,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> DispatchEventsAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var request = RpcJson.DecodeParameters<EventsParameters>(parameters);
        return await _workspace.GetEventsAsync(
            request.ThreadId,
            request.BeforeCursor,
            request.PageSize,
            request.MaximumBytes,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> DispatchImageAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var request = RpcJson.DecodeParameters<AttachmentParameters>(parameters);
        var image = await _images.GetAsync(
            request.ThreadId,
            request.AttachmentId,
            cancellationToken).ConfigureAwait(false);
        return new { contentType = image.ContentType, streamRequired = true };
    }

    private async Task<object> DispatchTextFileAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var request = RpcJson.DecodeParameters<TextFileParameters>(parameters);
        return await _files.GetAsync(request.ThreadId, request.FileId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> DispatchSubmissionAsync(
        JsonElement parameters,
        DevicePrincipal principal,
        CancellationToken cancellationToken)
    {
        var request = RpcJson.DecodeParameters<SubmitTextParameters>(parameters);
        if (request.CommandId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.ThreadId) ||
            string.IsNullOrWhiteSpace(request.Text) ||
            request.Text.Length > RpcJson.MaximumTextLength)
            throw new RemoteRpcException("invalid_parameters");
        if (_remoteSubmissions is null) throw new RemoteRpcException("submit_not_ready");
        var receipt = await _remoteSubmissions.SubmitOnceAsync(
            principal,
            request.CommandId,
            request.ThreadId,
            request.Text,
            cancellationToken).ConfigureAwait(false);
        return new
        {
            commandId = receipt.CommandId,
            state = receipt.State.ToString().ToLowerInvariant(),
            receipt.ErrorCode,
            receipt.UpdatedAt,
        };
    }

    private static object ValidatePendingCancellation(JsonElement parameters)
    {
        var request = RpcJson.DecodeParameters<CancelTransferParameters>(parameters);
        if (request.TransferId == Guid.Empty) throw new RemoteRpcException("invalid_parameters");
        throw new RemoteRpcException("transfer_not_ready");
    }

    private sealed class RemoteRpcException(string errorCode) : Exception(errorCode)
    {
        public string ErrorCode { get; } = errorCode;
    }
}
