using System.Collections.Concurrent;
using System.Text.Json;
using CodexBridge.Core;
using CodexBridge.Host.Auth;
using CodexBridge.Host.Remote;
using CodexBridge.Host.Services;
using CodexBridge.Remote.Protocol;

namespace CodexBridge.Host.Tests;

public sealed class RemoteConnectionServiceTests
{
    [Fact]
    public async Task Run_DispatchesReadOnlyRequestAndRejectsThirdSubscription()
    {
        var policy = new TargetPolicy();
        var catalog = new RemoteSubscriptionRegistryTests.FakeCatalog(
            RemoteSubscriptionRegistryTests.AllowedThreadIds);
        var reader = new RemoteSubscriptionRegistryTests.FakeReader();
        var textFiles = new TextFileAttachmentService(catalog, reader);
        var dispatcher = new RemoteRpcDispatcher(
            new WorkspaceQueryService(catalog, reader, policy, textFiles),
            new ImageAttachmentService(catalog, reader),
            textFiles,
            new DesktopStatusService());
        await using var subscriptions = new RemoteSubscriptionRegistry(
            catalog,
            policy,
            new ConversationStreamService(catalog, reader, policy));
        var service = new RemoteConnectionService(dispatcher, subscriptions);
        var projectsId = Guid.NewGuid();
        var thirdId = Guid.NewGuid();
        var transport = new FakeTransport([
            Request(projectsId, RpcMethod.Projects, new { }),
            Request(thirdId, RpcMethod.Subscribe, new
            {
                threadId = "01a00748-fa69-7e13-8800-74eadbd62cf7",
                afterSequence = 0,
            }),
        ]);

        await service.RunAsync(
            new DevicePrincipal("device", "phone", false),
            transport);

        var projects = DecodeResponse(transport.Sent.Single(frame => frame.RequestId == projectsId));
        var third = DecodeResponse(transport.Sent.Single(frame => frame.RequestId == thirdId));
        Assert.True(projects.Success);
        Assert.Equal("not_found", third.ErrorCode);
    }

    [Fact]
    public async Task Run_OversizedProjectsResponseDoesNotCloseConnectionOrBlockNextRequest()
    {
        var projects = Enumerable.Range(0, 2_000).Select(index => $@"D:\authorized\project-{index:D4}").ToArray();
        var policy = new TargetPolicy(projects);
        var catalog = new EmptyCatalog();
        var reader = new OversizedConversationReader();
        var textFiles = new TextFileAttachmentService(catalog, reader);
        var dispatcher = new RemoteRpcDispatcher(
            new WorkspaceQueryService(catalog, reader, policy, textFiles),
            new ImageAttachmentService(catalog, reader),
            textFiles,
            new DesktopStatusService());
        await using var subscriptions = new RemoteSubscriptionRegistry(
            catalog,
            policy,
            new ConversationStreamService(catalog, reader, policy));
        var service = new RemoteConnectionService(dispatcher, subscriptions);
        var projectsId = Guid.NewGuid();
        var statusId = Guid.NewGuid();
        var transport = new EncodingTransport([
            Request(projectsId, RpcMethod.Projects, new { }),
            Request(statusId, RpcMethod.Status, new { }),
        ]);

        await service.RunAsync(
            new DevicePrincipal("device", "phone", false),
            transport);

        var oversized = DecodeResponse(transport.Sent.Single(frame => frame.RequestId == projectsId));
        var status = DecodeResponse(transport.Sent.Single(frame => frame.RequestId == statusId));
        Assert.False(oversized.Success);
        Assert.Equal("response_too_large", oversized.ErrorCode);
        Assert.True(status.Success);
    }

    [Fact]
    public async Task Run_EventTextTooLargeDoesNotCloseConnectionOrBlockNextRequest()
    {
        var policy = new TargetPolicy();
        var catalog = new RemoteSubscriptionRegistryTests.FakeCatalog(
            RemoteSubscriptionRegistryTests.AllowedThreadIds);
        var reader = new OversizedConversationReader();
        var textFiles = new TextFileAttachmentService(catalog, reader);
        var workspace = new WorkspaceQueryService(catalog, reader, policy, textFiles);
        var dispatcher = new RemoteRpcDispatcher(
            workspace,
            new ImageAttachmentService(catalog, reader),
            textFiles,
            new DesktopStatusService());
        await using var subscriptions = new RemoteSubscriptionRegistry(
            catalog,
            policy,
            new ConversationStreamService(catalog, reader, policy));
        var transferId = Guid.NewGuid();
        var statusId = Guid.NewGuid();
        var transport = new CoordinatedTransport(
            Request(transferId, RpcMethod.EventText, new
            {
                threadId = RemoteSubscriptionRegistryTests.AllowedThreadIds[0],
                sequence = 1,
                contentId = "signed-content",
            }),
            Request(statusId, RpcMethod.Status, new { }),
            transferId);
        var service = new RemoteConnectionService(
            dispatcher,
            subscriptions,
            new RemoteAttachmentStreamer(new TooLargeEventTextSource()));

        await service.RunAsync(new DevicePrincipal("device", "phone", false), transport);

        var tooLarge = DecodeResponse(transport.Sent.Single(frame => frame.RequestId == transferId));
        var status = DecodeResponse(transport.Sent.Single(frame => frame.RequestId == statusId));
        Assert.Equal("content_too_large", tooLarge.ErrorCode);
        Assert.True(status.Success);
    }

    private static RemoteFrame Request(Guid id, RpcMethod method, object parameters) =>
        new(
            RemoteFrameKind.Request,
            id,
            0,
            RpcJson.Encode(new RpcRequest(
                method,
                JsonSerializer.SerializeToElement(parameters, RpcJson.Options))));

    private static RpcResponse DecodeResponse(RemoteFrame frame) =>
        JsonSerializer.Deserialize<RpcResponse>(frame.Payload.Span, RpcJson.Options)!;

    private sealed class FakeTransport(IReadOnlyList<RemoteFrame> incoming) : IRemoteFrameTransport
    {
        public ConcurrentBag<RemoteFrame> Sent { get; } = [];
        public async IAsyncEnumerable<RemoteFrame> ReadAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var frame in incoming)
            {
                yield return frame;
                await Task.Yield();
            }
        }
        public Task SendAsync(RemoteFrame frame, CancellationToken cancellationToken)
        {
            Sent.Add(frame);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EncodingTransport(IReadOnlyList<RemoteFrame> incoming) : IRemoteFrameTransport
    {
        public ConcurrentBag<RemoteFrame> Sent { get; } = [];

        public async IAsyncEnumerable<RemoteFrame> ReadAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var frame in incoming)
            {
                yield return frame;
                await Task.Yield();
            }
        }

        public Task SendAsync(RemoteFrame frame, CancellationToken cancellationToken)
        {
            RemoteFrameCodec.Encode(frame);
            Sent.Add(frame);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class OversizedConversationReader : IConversationReader
    {
        public async IAsyncEnumerable<ConversationEvent> ReadAsync(
            string rolloutPath,
            bool follow,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ConversationEvent(
                ConversationEventKind.AgentMessage,
                DateTimeOffset.UtcNow,
                new string('x', RemoteFrameCodec.MaximumPayloadLength),
                null,
                "agent_message");
            await Task.CompletedTask;
        }
    }

    private sealed class EmptyCatalog : IThreadCatalog
    {
        public Task<IReadOnlyList<ThreadSummary>> ListByProjectAsync(
            string projectPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ThreadSummary>>([]);

        public Task<ThreadSummary?> GetAsync(
            string threadId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ThreadSummary?>(null);
    }

    private sealed class TooLargeEventTextSource : IRemoteAttachmentSource
    {
        public Task<RemoteAttachmentContent> OpenImageAsync(
            string threadId, string attachmentId, CancellationToken cancellationToken) =>
            Task.FromException<RemoteAttachmentContent>(new KeyNotFoundException());

        public Task<RemoteAttachmentContent> OpenTextAsync(
            string threadId, string fileId, CancellationToken cancellationToken) =>
            Task.FromException<RemoteAttachmentContent>(new KeyNotFoundException());

        public Task<RemoteAttachmentContent> OpenEventTextAsync(
            string threadId, long sequence, string contentId, CancellationToken cancellationToken) =>
            Task.FromException<RemoteAttachmentContent>(new RemoteProtocolException(
                "content_too_large", "Event text exceeds its size limit."));
    }

    private sealed class CoordinatedTransport(
        RemoteFrame transferRequest,
        RemoteFrame statusRequest,
        Guid transferId) : IRemoteFrameTransport
    {
        public ConcurrentBag<RemoteFrame> Sent { get; } = [];

        public async IAsyncEnumerable<RemoteFrame> ReadAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return transferRequest;
            for (var attempt = 0; attempt < 100 && !Sent.Any(frame => frame.RequestId == transferId); attempt++)
                await Task.Delay(10, cancellationToken);
            yield return statusRequest;
        }

        public Task SendAsync(RemoteFrame frame, CancellationToken cancellationToken)
        {
            Sent.Add(frame);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
