using System.Collections.Concurrent;
using System.Text.Json;
using CodexBridge.Core;
using CodexBridge.Host.Services;
using CodexBridge.Remote.Protocol;

namespace CodexBridge.Host.Remote;

public sealed class RemoteSubscriptionRegistry : IAsyncDisposable
{
    private readonly IThreadCatalog _catalog;
    private readonly TargetPolicy _policy;
    private readonly ConversationStreamService _stream;
    private readonly ConcurrentDictionary<string, Subscription> _subscriptions =
        new(StringComparer.Ordinal);

    public RemoteSubscriptionRegistry(
        IThreadCatalog catalog,
        TargetPolicy policy,
        ConversationStreamService stream)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public async Task SubscribeAsync(
        string threadId,
        Guid requestId,
        long afterSequence,
        Func<RemoteFrame, CancellationToken, Task> sendAsync,
        CancellationToken connectionCancellation)
    {
        ThreadSummary? thread = null;
        if (afterSequence >= 0)
        {
            try { thread = await _catalog.GetAsync(threadId, connectionCancellation).ConfigureAwait(false); }
            catch (UnauthorizedAccessException) { }
        }
        if (afterSequence < 0 || thread is null)
            throw new KeyNotFoundException("Thread does not exist.");

        await UnsubscribeAsync(threadId).ConfigureAwait(false);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(connectionCancellation);
        var subscription = new Subscription(cancellation);
        if (!_subscriptions.TryAdd(threadId, subscription))
        {
            cancellation.Dispose();
            throw new InvalidOperationException("Subscription could not be registered.");
        }

        subscription.Runner = Task.Run(async () =>
        {
            try
            {
                await foreach (var item in _stream.StreamAsync(
                                   threadId,
                                   afterSequence,
                                   cancellation.Token))
                {
                    var payload = RpcJson.Encode(new
                    {
                        threadId,
                        item.Sequence,
                        item.Kind,
                        item.Timestamp,
                        item.Text,
                        item.TurnId,
                        item.Images,
                        item.Files,
                        item.TextPreview,
                        item.TextContentId,
                        item.TextLength,
                    });
                    await sendAsync(
                        new RemoteFrame(
                            RemoteFrameKind.Event,
                            requestId,
                            item.Sequence,
                            payload),
                        cancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }, CancellationToken.None);
    }

    public async Task UnsubscribeAsync(string threadId)
    {
        if (!_subscriptions.TryRemove(threadId, out var subscription)) return;
        subscription.Cancellation.Cancel();
        try { await subscription.Runner.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally { subscription.Cancellation.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var threadId in _subscriptions.Keys.ToArray())
            await UnsubscribeAsync(threadId).ConfigureAwait(false);
    }

    private sealed class Subscription(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Runner { get; set; } = Task.CompletedTask;
    }
}
