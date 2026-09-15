using System.Text.Json;
using CodexBridge.Core;
using CodexBridge.Host.Auth;
using CodexBridge.Host.Remote;
using CodexBridge.Host.Services;
using CodexBridge.Remote.Protocol;
using CodexBridge.Windows;
using CodexBridge.Entitlements;

namespace CodexBridge.Host.Tests;

public sealed class RemoteRpcDispatcherTests : IDisposable
{
    private readonly string _directory = TestPaths.CreateDirectory("remote-rpc");

    [Fact]
    public async Task Dispatch_ReturnsOnlyAllowedProjectAndThreads()
    {
        var dispatcher = CreateDispatcher();
        var projects = await DispatchAsync(dispatcher, RpcMethod.Projects, new { });
        var projectId = projects.Result!.Value[0].GetProperty("id").GetString()!;
        var threads = await DispatchAsync(dispatcher, RpcMethod.Threads, new
        {
            projectId,
            cursor = (string?)null,
            pageSize = 30,
            query = (string?)null,
        });

        Assert.True(projects.Success);
        var items = threads.Result!.Value.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.DoesNotContain(
            items.EnumerateArray(),
            item => item.GetProperty("id").GetString() == "01a00748-fa69-7e13-8800-74eadbd62cf7");
    }

    [Fact]
    public async Task Dispatch_RejectsThirdThreadAndUnknownFields()
    {
        var dispatcher = CreateDispatcher();
        var third = await DispatchAsync(dispatcher, RpcMethod.Events, new
        {
            threadId = "01a00748-fa69-7e13-8800-74eadbd62cf7",
            beforeCursor = (string?)null,
            pageSize = 40,
            maximumBytes = RpcJson.MaximumResponsePayloadLength,
        });
        var path = await DispatchAsync(dispatcher, RpcMethod.Events, new
        {
            threadId = RemoteSubscriptionRegistryTests.AllowedThreadIds[0],
            beforeCursor = (string?)null,
            pageSize = 40,
            maximumBytes = RpcJson.MaximumResponsePayloadLength,
            path = @"C:\secret",
        });

        Assert.Equal("not_found", third.ErrorCode);
        Assert.Equal("invalid_parameters", path.ErrorCode);
    }

    [Fact]
    public async Task Dispatch_ReadOnlyDeviceCannotSubmit()
    {
        var response = await DispatchAsync(CreateDispatcher(), RpcMethod.SubmitText, new
        {
            commandId = Guid.NewGuid(),
            threadId = RemoteSubscriptionRegistryTests.AllowedThreadIds[0],
            text = "test",
        }, canSend: false);
        Assert.Equal("forbidden_read_only", response.ErrorCode);
    }

    [Fact]
    public async Task Dispatch_SubmitReturnsStableDesktopErrorWithoutBreakingRpc()
    {
        var response = await DispatchAsync(CreateDispatcher(new OfflineSender()), RpcMethod.SubmitText, new
        {
            commandId = Guid.NewGuid(),
            threadId = RemoteSubscriptionRegistryTests.AllowedThreadIds[0],
            text = "RPC-SECRET-MUST-NOT-BE-STORED",
        });

        Assert.False(response.Success);
        Assert.Equal("desktop_unavailable", response.ErrorCode);
        Assert.DoesNotContain(
            "RPC-SECRET-MUST-NOT-BE-STORED",
            await File.ReadAllTextAsync(Path.Combine(_directory, "receipts.json")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispatch_HostEntitlementOverridesTamperedPrincipalOnEveryRequest()
    {
        var sender = new RecordingSender();
        var dispatcher = CreateDispatcher(sender, new FixedCapabilityResolver(canSend: false));
        var response = await DispatchAsync(dispatcher, RpcMethod.SubmitText, new
        {
            commandId = Guid.NewGuid(),
            threadId = RemoteSubscriptionRegistryTests.AllowedThreadIds[0],
            text = "must be rejected",
        }, canSend: true);

        Assert.Equal("forbidden_read_only", response.ErrorCode);
        Assert.Equal(0, sender.CallCount);
    }

    private static async Task<RpcResponse> DispatchAsync(
        RemoteRpcDispatcher dispatcher,
        RpcMethod method,
        object parameters,
        bool canSend = true)
    {
        var request = new RpcRequest(method, JsonSerializer.SerializeToElement(parameters, RpcJson.Options));
        var encoded = await dispatcher.DispatchAsync(
            new DevicePrincipal("device", "test", canSend),
            RpcJson.Encode(request));
        return JsonSerializer.Deserialize<RpcResponse>(encoded, RpcJson.Options)!;
    }

    private RemoteRpcDispatcher CreateDispatcher(
        ICodexDesktopSender? sender = null,
        IRemoteCapabilityResolver? capabilityResolver = null)
    {
        var policy = new TargetPolicy();
        var catalog = new FakeCatalog();
        var reader = new FakeReader();
        var textFiles = new TextFileAttachmentService(catalog, reader);
        RemoteMessageSubmissionService? remoteSubmissions = null;
        if (sender is not null)
        {
            var submissions = new MessageSubmissionService(
                catalog,
                sender,
                new DesktopCommandQueue(),
                new AuditLog(Path.Combine(_directory, "audit.jsonl"), TimeProvider.System),
                policy,
                TimeProvider.System);
            remoteSubmissions = new RemoteMessageSubmissionService(
                new RemoteCommandReceiptStore(Path.Combine(_directory, "receipts.json"), TimeProvider.System),
                submissions);
        }
        return new RemoteRpcDispatcher(
            new WorkspaceQueryService(catalog, reader, policy, textFiles),
            new ImageAttachmentService(catalog, reader),
            textFiles,
            new DesktopStatusService(),
            remoteSubmissions,
            capabilityResolver);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class OfflineSender : ICodexDesktopSender
    {
        public Task<SendResult> SendAsync(
            string projectPath,
            string threadId,
            string message,
            CancellationToken cancellationToken = default) =>
            throw new DesktopUnavailableException("offline");
    }

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
            return Task.FromResult(new SendResult(threadId, "thread", false));
        }
    }

    private sealed class FixedCapabilityResolver(bool canSend) : IRemoteCapabilityResolver
    {
        public DevicePrincipal Resolve(DevicePrincipal principal) => principal with { CanSend = canSend };
        public EntitlementDecision Evaluate(string deviceId) => new(
            canSend, canSend ? EntitlementState.Pro : EntitlementState.Free, null, null, null);
        public bool Remove(string deviceId) => false;
    }

    private sealed class FakeCatalog : IThreadCatalog
    {
        private static readonly ThreadSummary[] Threads = RemoteSubscriptionRegistryTests.AllowedThreadIds
            .Select((id, index) => new ThreadSummary(
                id,
                $"thread-{index}",
                TargetPolicy.AllowedProjectPath,
                "preview",
                index,
                false,
                $"{index}.jsonl",
                "test"))
            .ToArray();
        public Task<IReadOnlyList<ThreadSummary>> ListByProjectAsync(string projectPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ThreadSummary>>(Threads);
        public Task<ThreadSummary?> GetAsync(string threadId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Threads.FirstOrDefault(thread => thread.Id == threadId));
    }

    private sealed class FakeReader : IConversationReader
    {
        public async IAsyncEnumerable<ConversationEvent> ReadAsync(
            string rolloutPath,
            bool follow,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ConversationEvent(
                ConversationEventKind.AgentMessage,
                DateTimeOffset.UtcNow,
                "visible",
                null,
                "agent_message");
            await Task.CompletedTask;
        }
    }
}
