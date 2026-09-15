using CodexBridge.Core;
using CodexBridge.Host.Contracts;
using CodexBridge.Host.Services;
using CodexBridge.Remote.Protocol;
using CodexBridge.Windows;
using System.Text;
using System.Text.Json;

namespace CodexBridge.Host.Tests;

public sealed class WorkspaceQueryServiceTests
{
    private const string ProjectOne = @"D:\authorized\project-one";
    private const string ProjectTwo = @"D:\authorized\project-two";

    [Fact]
    public async Task Queries_ReturnTwoAuthorizedProjectsWithStableIdsAndIndependentCounts()
    {
        var catalog = new FakeCatalog();
        var reader = new FakeConversationReader();
        var service = new WorkspaceQueryService(
            catalog,
            reader,
            new TargetPolicy([ProjectOne, ProjectTwo]));

        var projects = await service.GetProjectsAsync();
        var firstProject = Assert.Single(projects, project => project.Name == "project-one");
        var secondProject = Assert.Single(projects, project => project.Name == "project-two");
        var firstThreads = await service.GetThreadsAsync(firstProject.Id, pageSize: 10);
        var secondThreads = await service.GetThreadsAsync(secondProject.Id, pageSize: 10);
        var events = await service.GetEventsAsync(firstThreads.Items[0].Id);

        Assert.Equal(2, firstProject.ThreadCount);
        Assert.Equal(1, secondProject.ThreadCount);
        Assert.All(firstThreads.Items, thread => Assert.Equal("idle", thread.Status));
        Assert.Single(secondThreads.Items);
        Assert.Equal("duplicate-title", firstThreads.Items[0].Title);
        Assert.Equal("duplicate-title", secondThreads.Items[0].Title);
        Assert.Equal(
            firstProject.Id,
            WorkspaceQueryService.CreateProjectId(ProjectOne.ToUpperInvariant() + "\\"));
        Assert.DoesNotContain(events.Items, item => item.Kind == nameof(ConversationEventKind.Unknown));
        Assert.Equal([1L, 2L], events.Items.Select(item => item.Sequence));
    }

    [Fact]
    public async Task Queries_RejectUnknownProjectAndUnauthorizedThread()
    {
        var service = new WorkspaceQueryService(
            new FakeCatalog(),
            new FakeConversationReader(),
            new TargetPolicy([ProjectOne, ProjectTwo]));

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.GetThreadsAsync(WorkspaceQueryService.CreateProjectId(@"D:\unauthorized")));
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.GetEventsAsync("outside-thread"));
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.GetEventsAsync("missing-thread"));
    }

    [Fact]
    public async Task Threads_PageWithStableOrderSearchAndSignedCursor()
    {
        var project = @"D:\authorized\large";
        var catalog = new LargeCatalog(project);
        var service = new WorkspaceQueryService(
            catalog,
            new FakeConversationReader(),
            new TargetPolicy([project]));
        var projectId = WorkspaceQueryService.CreateProjectId(project);

        var first = await service.GetThreadsAsync(projectId, pageSize: 30);
        var second = await service.GetThreadsAsync(projectId, first.NextCursor, pageSize: 30);
        var third = await service.GetThreadsAsync(projectId, second.NextCursor, pageSize: 30);
        var all = first.Items.Concat(second.Items).Concat(third.Items).ToArray();

        Assert.Equal([30, 30, 15], [first.Items.Count, second.Items.Count, third.Items.Count]);
        Assert.True(first.HasMore);
        Assert.True(second.HasMore);
        Assert.False(third.HasMore);
        Assert.Equal(75, first.TotalApproximate);
        Assert.Equal(75, all.Select(thread => thread.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            all.OrderByDescending(thread => thread.UpdatedAtMs).ThenBy(thread => thread.Id, StringComparer.Ordinal),
            all);

        var search = await service.GetThreadsAsync(projectId, pageSize: 10, query: "needle");
        Assert.Equal(2, search.TotalApproximate);
        Assert.Equal(["thread-007", "thread-042"], search.Items.Select(thread => thread.Id).Order());

        var cursor = first.NextCursor!;
        var signatureStart = cursor.LastIndexOf('.') + 1;
        var tampered = cursor[..signatureStart] +
            (cursor[signatureStart] == 'A' ? 'B' : 'A') +
            cursor[(signatureStart + 1)..];
        var exception = await Assert.ThrowsAsync<RemoteProtocolException>(
            () => service.GetThreadsAsync(projectId, tampered, pageSize: 30));
        Assert.Equal("invalid_cursor", exception.ErrorCode);

        var changedQuery = await Assert.ThrowsAsync<RemoteProtocolException>(
            () => service.GetThreadsAsync(projectId, first.NextCursor, pageSize: 30, query: "different"));
        Assert.Equal("invalid_cursor", changedQuery.ErrorCode);
    }

    [Fact]
    public async Task Events_PageBackwardWithoutDuplicatesAndHonorsEncodedByteBudget()
    {
        var fixture = CreateEventFixture(205, index =>
            index % 2 == 0
                ? $"event-{index:D3}-" + new string('a', 700)
                : $"事件-{index:D3}-" + new string('中', 260));

        var cursor = (string?)null;
        var received = new List<ConversationEventDto>();
        var pageSizes = new List<int>();
        const int maximumBytes = 12 * 1024;
        do
        {
            var page = await fixture.Service.GetEventsAsync(
                fixture.ThreadId,
                cursor,
                pageSize: 40,
                maximumBytes: maximumBytes);

            Assert.True(EncodedPageLength(page) <= maximumBytes);
            received.InsertRange(0, page.Items);
            pageSizes.Add(page.Items.Count);
            cursor = page.PreviousCursor;
        }
        while (cursor is not null);

        Assert.Equal(205, received.Count);
        Assert.Equal(205, received.Select(item => item.Sequence).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 205).Select(index => (long)index), received.Select(item => item.Sequence));
        Assert.All(pageSizes, size => Assert.InRange(size, 1, 40));
    }

    [Fact]
    public async Task Events_ReturnLatestPageAndRejectTamperedCursor()
    {
        var fixture = CreateEventFixture(205, index => $"event-{index}");

        var latest = await fixture.Service.GetEventsAsync(fixture.ThreadId, pageSize: 40);

        Assert.Equal(205, latest.LatestSequence);
        Assert.Equal(Enumerable.Range(166, 40).Select(index => (long)index), latest.Items.Select(item => item.Sequence));
        Assert.True(latest.HasMoreBefore);
        Assert.NotNull(latest.PreviousCursor);

        var cursor = latest.PreviousCursor!;
        var tampered = cursor[..^1] + (cursor[^1] == 'A' ? 'B' : 'A');
        var exception = await Assert.ThrowsAsync<RemoteProtocolException>(
            () => fixture.Service.GetEventsAsync(fixture.ThreadId, tampered, pageSize: 40));
        Assert.Equal("invalid_cursor", exception.ErrorCode);
    }

    [Fact]
    public async Task Events_CursorSurvivesAppendButBecomesStaleAfterTruncate()
    {
        var fixture = CreateEventFixture(80, index => $"event-{index}");
        var latest = await fixture.Service.GetEventsAsync(fixture.ThreadId, pageSize: 40);
        var cursor = Assert.IsType<string>(latest.PreviousCursor);

        fixture.Reader.Add("appended event");
        File.AppendAllText(fixture.RolloutPath, "appended\n");
        var older = await fixture.Service.GetEventsAsync(fixture.ThreadId, cursor, pageSize: 40);

        Assert.Equal(81, older.LatestSequence);
        Assert.Equal(Enumerable.Range(1, 40).Select(index => (long)index), older.Items.Select(item => item.Sequence));

        File.WriteAllText(fixture.RolloutPath, "x");
        var exception = await Assert.ThrowsAsync<RemoteProtocolException>(
            () => fixture.Service.GetEventsAsync(fixture.ThreadId, cursor, pageSize: 40));
        Assert.Equal("cursor_stale", exception.ErrorCode);
    }

    [Theory]
    [InlineData(63 * 1024)]
    [InlineData(65 * 1024)]
    [InlineData(1024 * 1024)]
    [InlineData(4 * 1024 * 1024)]
    public async Task EventText_LargeMessagesUseBoundPreviewAndStreamFullContent(int utf8Length)
    {
        var text = new string('x', utf8Length);
        var fixture = CreateEventFixture(1, _ => text);

        var page = await fixture.Service.GetEventsAsync(fixture.ThreadId, pageSize: 10);
        var item = Assert.Single(page.Items);

        Assert.Null(item.Text);
        Assert.NotNull(item.TextContentId);
        Assert.Equal(utf8Length, item.TextLength);
        Assert.InRange(Encoding.UTF8.GetByteCount(item.TextPreview!), 1, WorkspaceQueryService.InlineEventTextBytes);

        var content = await fixture.Service.GetEventTextAsync(
            fixture.ThreadId, item.Sequence, item.TextContentId!);
        Assert.Equal(utf8Length, content.Utf8Length);
        Assert.Equal(text, content.Content);
    }

    [Fact]
    public async Task EventText_RejectsOverLimitCrossThreadWrongSequenceAndForgedId()
    {
        var text = new string('x', WorkspaceQueryService.MaximumEventTextBytes + 1);
        var fixture = CreateEventFixture(1, _ => text);
        var page = await fixture.Service.GetEventsAsync(fixture.ThreadId, pageSize: 10);
        var item = Assert.Single(page.Items);
        var contentId = Assert.IsType<string>(item.TextContentId);

        var tooLarge = await Assert.ThrowsAsync<RemoteProtocolException>(
            () => fixture.Service.GetEventTextAsync(fixture.ThreadId, item.Sequence, contentId));
        Assert.Equal("content_too_large", tooLarge.ErrorCode);

        var crossThread = await Assert.ThrowsAsync<RemoteProtocolException>(
            () => fixture.Service.GetEventTextAsync("other-thread", item.Sequence, contentId));
        Assert.Equal("invalid_content_id", crossThread.ErrorCode);

        var wrongSequence = await Assert.ThrowsAsync<RemoteProtocolException>(
            () => fixture.Service.GetEventTextAsync(fixture.ThreadId, item.Sequence + 1, contentId));
        Assert.Equal("invalid_content_id", wrongSequence.ErrorCode);

        var signatureStart = contentId.LastIndexOf('.') + 1;
        var forged = contentId[..signatureStart] +
            (contentId[signatureStart] == 'A' ? 'B' : 'A') +
            contentId[(signatureStart + 1)..];
        var forgedId = await Assert.ThrowsAsync<RemoteProtocolException>(
            () => fixture.Service.GetEventTextAsync(fixture.ThreadId, item.Sequence, forged));
        Assert.Equal("invalid_content_id", forgedId.ErrorCode);
    }

    [Fact]
    public async Task Threads_PageTenThousandGeneratedSessionsWithoutReturningAllItems()
    {
        var project = @"D:\authorized\huge";
        var service = new WorkspaceQueryService(
            new LargeCatalog(project, 10_000),
            new FakeConversationReader(),
            new TargetPolicy([project]));
        var projectId = WorkspaceQueryService.CreateProjectId(project);

        var first = await service.GetThreadsAsync(projectId, pageSize: 50);
        var second = await service.GetThreadsAsync(projectId, first.NextCursor, pageSize: 50);

        Assert.Equal(10_000, first.TotalApproximate);
        Assert.Equal(50, first.Items.Count);
        Assert.Equal(50, second.Items.Count);
        Assert.Empty(first.Items.Select(item => item.Id).Intersect(second.Items.Select(item => item.Id)));
    }

    [Fact]
    public async Task IndexedWorkspaceReadsOnlyRequestedHistoryPageAndThreadStatus()
    {
        var directory = TestPaths.CreateDirectory("indexed-workspace");
        var rollout = Path.Combine(directory, "rollout.jsonl");
        try
        {
            await using (var writer = new StreamWriter(rollout, false, new UTF8Encoding(false)))
            {
                for (var index = 1; index <= 204; index++)
                    await writer.WriteLineAsync(RolloutLine("agent_message", $"event-{index}"));
                await writer.WriteLineAsync(RolloutLine("task_started", null));
            }
            const string threadId = "indexed-thread";
            var catalog = new SingleThreadCatalog(new ThreadSummary(
                threadId, "indexed", directory, "preview", 1, false, rollout, "test"));
            var indexStore = new RolloutIndexStore(Path.Combine(directory, "indexes"));
            var service = new WorkspaceQueryService(
                catalog,
                new RolloutConversationReader(),
                new TargetPolicy([directory]),
                index: indexStore);

            var latest = await service.GetEventsAsync(threadId, pageSize: 40);
            var older = await service.GetEventsAsync(threadId, latest.PreviousCursor, pageSize: 40);
            var threads = await service.GetThreadsAsync(
                WorkspaceQueryService.CreateProjectId(directory), pageSize: 10);

            Assert.Equal(205, latest.LatestSequence);
            Assert.Equal(Enumerable.Range(166, 40).Select(value => (long)value), latest.Items.Select(item => item.Sequence));
            Assert.Equal(Enumerable.Range(126, 40).Select(value => (long)value), older.Items.Select(item => item.Sequence));
            Assert.Equal("running", Assert.Single(threads.Items).Status);
            Assert.Equal("event-204", Assert.Single(threads.Items).Preview);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static EventFixture CreateEventFixture(int count, Func<int, string> createText)
    {
        var project = Path.Combine(Directory.GetCurrentDirectory(), ".test-state", "event-paging");
        Directory.CreateDirectory(project);
        var rolloutPath = Path.Combine(project, $"{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(rolloutPath, new string('r', 4096));
        var reader = new MutableConversationReader(
            Enumerable.Range(1, count).Select(createText));
        const string threadId = "event-thread";
        var catalog = new SingleThreadCatalog(new ThreadSummary(
            threadId,
            "event paging",
            project,
            "preview",
            1,
            false,
            rolloutPath,
            "test"));
        var service = new WorkspaceQueryService(catalog, reader, new TargetPolicy([project]));
        return new EventFixture(service, reader, threadId, rolloutPath);
    }

    private static int EncodedPageLength(ConversationEventPageDto page) => RpcJson.Encode(new RpcResponse(
        true,
        JsonSerializer.SerializeToElement(page, RpcJson.Options),
        null)).Length;

    private sealed class FakeCatalog : IThreadCatalog
    {
        private static readonly ThreadSummary[] Threads =
        [
            new(
                "one-running",
                "duplicate-title",
                ProjectOne,
                "预览1",
                200,
                false,
                "one.jsonl",
                "custom"),
            new(
                "one-idle",
                "one-idle-title",
                ProjectOne,
                "预览2",
                100,
                false,
                "two.jsonl",
                "custom"),
            new(
                "two-idle",
                "duplicate-title",
                ProjectTwo,
                "预览3",
                300,
                false,
                "two.jsonl",
                "custom"),
            new(
                "outside-thread",
                "outside",
                @"D:\unauthorized",
                "hidden",
                400,
                false,
                "outside.jsonl",
                "custom")
        ];

        public Task<IReadOnlyList<ThreadSummary>> ListByProjectAsync(
            string projectPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ThreadSummary>>(Threads
                .Where(thread => string.Equals(thread.Cwd, projectPath, StringComparison.OrdinalIgnoreCase))
                .ToArray());

        public Task<ThreadSummary?> GetAsync(
            string threadId,
            CancellationToken cancellationToken = default)
        {
            var thread = Threads.FirstOrDefault(thread => thread.Id == threadId);
            if (thread?.Id == "outside-thread")
            {
                throw new UnauthorizedAccessException();
            }

            return Task.FromResult(thread);
        }
    }

    private sealed class FakeConversationReader : IConversationReader
    {
        public async IAsyncEnumerable<ConversationEvent> ReadAsync(
            string rolloutPath,
            bool follow,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            yield return new ConversationEvent(
                ConversationEventKind.UserMessage,
                DateTimeOffset.UtcNow,
                "测试",
                null,
                "user_message");
            yield return new ConversationEvent(
                ConversationEventKind.Unknown,
                DateTimeOffset.UtcNow,
                "hidden",
                null,
                "internal");
            yield return new ConversationEvent(
                ConversationEventKind.TaskStarted,
                DateTimeOffset.UtcNow,
                null,
                "turn-1",
                "task_started");
            if (rolloutPath == "two.jsonl")
            {
                yield return new ConversationEvent(
                    ConversationEventKind.TaskCompleted,
                    DateTimeOffset.UtcNow,
                    null,
                    "turn-1",
                    "task_complete");
            }

            await Task.CompletedTask;
        }
    }

    private sealed class LargeCatalog(string projectPath, int count = 75) : IThreadCatalog
    {
        private readonly ThreadSummary[] _threads = Enumerable.Range(0, count)
            .Select(index => new ThreadSummary(
                $"thread-{index:D3}",
                index == 7 ? "needle title" : $"thread {index}",
                projectPath,
                index == 42 ? "preview with needle" : $"preview {index}",
                10_000 - index / 3,
                false,
                $"{index}.jsonl",
                "test"))
            .ToArray();

        public Task<IReadOnlyList<ThreadSummary>> ListByProjectAsync(
            string requestedProjectPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ThreadSummary>>(_threads);

        public Task<ThreadSummary?> GetAsync(
            string threadId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_threads.FirstOrDefault(thread => thread.Id == threadId));
    }

    private sealed class SingleThreadCatalog(ThreadSummary thread) : IThreadCatalog
    {
        public Task<IReadOnlyList<ThreadSummary>> ListByProjectAsync(
            string projectPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ThreadSummary>>([thread]);

        public Task<ThreadSummary?> GetAsync(
            string threadId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ThreadSummary?>(thread.Id == threadId
                ? thread
                : threadId == "other-thread" ? thread with { Id = threadId } : null);
    }

    private sealed class MutableConversationReader(IEnumerable<string> texts) : IConversationReader
    {
        private readonly List<string> _texts = texts.ToList();

        public void Add(string text) => _texts.Add(text);

        public async IAsyncEnumerable<ConversationEvent> ReadAsync(
            string rolloutPath,
            bool follow,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            foreach (var text in _texts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ConversationEvent(
                    ConversationEventKind.AgentMessage,
                    DateTimeOffset.UtcNow,
                    text,
                    null,
                    "assistant_message");
            }

            await Task.CompletedTask;
        }
    }

    private sealed record EventFixture(
        WorkspaceQueryService Service,
        MutableConversationReader Reader,
        string ThreadId,
        string RolloutPath);

    private static string RolloutLine(string type, string? message) => JsonSerializer.Serialize(new
    {
        timestamp = "2026-08-22T00:00:00Z",
        type = "event_msg",
        payload = new { type, message },
    });
}
