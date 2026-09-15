using System.Collections.Concurrent;
using CodexBridge.Core;
using CodexBridge.Host.Remote;
using CodexBridge.Host.Services;
using CodexBridge.Remote.Protocol;

namespace CodexBridge.Host.Tests;

public sealed class RemoteSubscriptionRegistryTests
{
    internal static readonly string[] AllowedThreadIds =
    [
        "01a00749-2fb0-7fa0-9186-4f8292732f2c",
        "01a00749-6d7c-7072-9b22-f4a70ea35331",
    ];

    [Fact]
    public async Task Subscribe_StreamsTwoAllowedThreadsIndependently()
    {
        var (registry, catalog) = CreateRegistry();
        await using (registry)
        {
            var frames = new ConcurrentBag<RemoteFrame>();
            Task Send(RemoteFrame frame, CancellationToken _) { frames.Add(frame); return Task.CompletedTask; }
            var ids = catalog.Threads.Select(thread => thread.Id).ToArray();

            await registry.SubscribeAsync(ids[0], Guid.NewGuid(), 0, Send, CancellationToken.None);
            await registry.SubscribeAsync(ids[1], Guid.NewGuid(), 1, Send, CancellationToken.None);
            await WaitUntilAsync(() => frames.Count >= 3);

            Assert.Single(frames.Where(frame => frame.Sequence == 1));
            Assert.Equal(2, frames.Count(frame => frame.Sequence == 2));
            Assert.All(frames, frame => Assert.Equal(RemoteFrameKind.Event, frame.Kind));
        }
    }

    [Fact]
    public async Task Subscribe_RejectsUnallowedThirdThreadBeforeStartingStream()
    {
        var (registry, _) = CreateRegistry();
        await using (registry)
        {
            await Assert.ThrowsAsync<KeyNotFoundException>(() => registry.SubscribeAsync(
                "01a00748-fa69-7e13-8800-74eadbd62cf7",
                Guid.NewGuid(),
                0,
                (_, _) => Task.CompletedTask,
                CancellationToken.None));
        }
    }

    private static (RemoteSubscriptionRegistry Registry, FakeCatalog Catalog) CreateRegistry()
    {
        var policy = new TargetPolicy();
        var catalog = new FakeCatalog(AllowedThreadIds);
        var stream = new ConversationStreamService(catalog, new FakeReader(), policy);
        return (new RemoteSubscriptionRegistry(catalog, policy, stream), catalog);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    internal sealed class FakeCatalog(IEnumerable<string> ids) : IThreadCatalog
    {
        public ThreadSummary[] Threads { get; } = ids.Select((id, index) => new ThreadSummary(
            id, $"thread-{index}", TargetPolicy.AllowedProjectPath, "preview", index, false,
            $"rollout-{index}", "test")).ToArray();
        public Task<IReadOnlyList<ThreadSummary>> ListByProjectAsync(string projectPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ThreadSummary>>(Threads);
        public Task<ThreadSummary?> GetAsync(string threadId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Threads.FirstOrDefault(thread => thread.Id == threadId));
    }

    internal sealed class FakeReader : IConversationReader
    {
        public async IAsyncEnumerable<ConversationEvent> ReadAsync(
            string rolloutPath,
            bool follow,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (var sequence = 1; sequence <= 2; sequence++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ConversationEvent(
                    ConversationEventKind.AgentMessage,
                    DateTimeOffset.UtcNow,
                    $"{rolloutPath}-{sequence}",
                    null,
                    "agent_message");
            }
            await Task.CompletedTask;
        }
    }
}
