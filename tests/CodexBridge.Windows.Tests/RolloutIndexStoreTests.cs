using System.Text;
using System.Text.Json;
using CodexBridge.Core;

namespace CodexBridge.Windows.Tests;

public sealed class RolloutIndexStoreTests : IDisposable
{
    private readonly string _directory = TestPaths.CreateDirectory("rollout-index");

    [Fact]
    public async Task IndexesMixedFormatsOffsetsTimestampsAndLatestStatus()
    {
        var rollout = Path.Combine(_directory, "mixed.jsonl");
        var lines = new[]
        {
            Visible("user_message", "old", "2026-08-20T01:00:00Z"),
            "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\"}}",
            Modern("AgentMessage", "new", "2026-08-20T02:00:00Z"),
            Visible("task_started", null, "2026-08-20T03:00:00Z"),
            Visible("task_complete", null, "2026-08-20T04:00:00Z"),
        };
        await File.WriteAllTextAsync(rollout, string.Join('\n', lines) + "\n", Encoding.UTF8);
        var store = new RolloutIndexStore(Path.Combine(_directory, "indexes"));

        var summary = await store.GetSummaryAsync(rollout);
        var page = await store.GetPageAsync(rollout, long.MaxValue, 10);

        Assert.Equal(4, summary.LatestSequence);
        Assert.Equal("idle", summary.Status);
        Assert.Equal([1L, 2L, 3L, 4L], summary.Entries.Select(entry => entry.Sequence));
        Assert.True(summary.Entries.Zip(summary.Entries.Skip(1)).All(pair => pair.First.Offset < pair.Second.Offset));
        Assert.Equal(ConversationEventKind.UserMessage, summary.Entries[0].Kind);
        Assert.Equal(ConversationEventKind.AgentMessage, summary.Entries[1].Kind);
        Assert.Equal(DateTimeOffset.Parse("2026-08-20T02:00:00Z"), summary.Entries[1].Timestamp);
        Assert.Equal(["old", "new", null, null], page.Select(item => item.Event.Text));
    }

    [Fact]
    public async Task AppendsFromIndexedBoundaryAndRebuildsAfterTruncateOrCorruption()
    {
        var rollout = Path.Combine(_directory, "append.jsonl");
        await File.WriteAllTextAsync(rollout, Visible("user_message", "one") + "\n", Encoding.UTF8);
        var indexDirectory = Path.Combine(_directory, "indexes");
        var store = new RolloutIndexStore(indexDirectory);
        var first = await store.GetSummaryAsync(rollout);

        await File.AppendAllTextAsync(rollout, Visible("agent_message", "two") + "\n", Encoding.UTF8);
        var appended = await store.GetSummaryAsync(rollout);
        Assert.Equal(2, appended.LatestSequence);
        Assert.Equal(first.IndexedLength, appended.Entries[1].Offset);

        await File.WriteAllTextAsync(rollout, Visible("task_started", null) + "\n", Encoding.UTF8);
        var rebuilt = await store.GetSummaryAsync(rollout);
        Assert.Equal(1, rebuilt.LatestSequence);
        Assert.Equal("running", rebuilt.Status);

        var indexPath = Assert.Single(Directory.GetFiles(indexDirectory, "*.json"));
        await File.WriteAllTextAsync(indexPath, "{broken", Encoding.UTF8);
        var recovered = await new RolloutIndexStore(indexDirectory).GetSummaryAsync(rollout);
        Assert.Equal(1, recovered.LatestSequence);
        Assert.Equal(ConversationEventKind.TaskStarted, recovered.Entries[0].Kind);
    }

    [Fact]
    public async Task TemporaryIndexDoesNotReplaceLastCompleteIndex()
    {
        var rollout = Path.Combine(_directory, "temporary.jsonl");
        await File.WriteAllTextAsync(rollout, Visible("user_message", "one") + "\n", Encoding.UTF8);
        var indexDirectory = Path.Combine(_directory, "indexes");
        var store = new RolloutIndexStore(indexDirectory);
        await store.GetSummaryAsync(rollout);
        var indexPath = Assert.Single(Directory.GetFiles(indexDirectory, "*.json"));
        await File.WriteAllTextAsync(indexPath + ".tmp", "{incomplete", Encoding.UTF8);

        var summary = await new RolloutIndexStore(indexDirectory).GetSummaryAsync(rollout);

        Assert.Equal(1, summary.LatestSequence);
        Assert.True(File.Exists(indexPath));
    }

    [Fact]
    public async Task IndexesOneHundredThousandEventsWithoutLargeFixture()
    {
        var rollout = Path.Combine(_directory, "large.jsonl");
        await using (var writer = new StreamWriter(rollout, false, new UTF8Encoding(false)))
        {
            for (var index = 0; index < 100_000; index++)
                await writer.WriteLineAsync(Visible(index % 2 == 0 ? "user_message" : "agent_message", $"event-{index}"));
        }
        var store = new RolloutIndexStore(Path.Combine(_directory, "large-index"));

        var summary = await store.GetSummaryAsync(rollout);
        var page = await store.GetPageAsync(rollout, 100_001, 40);

        Assert.Equal(100_000, summary.LatestSequence);
        Assert.Equal(Enumerable.Range(99_961, 40).Select(value => (long)value), page.Select(value => value.Sequence));
        Assert.Equal("event-99999", page[^1].Event.Text);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static string Visible(string type, string? message, string timestamp = "2026-08-22T00:00:00Z") =>
        JsonSerializer.Serialize(new
        {
            timestamp,
            type = "event_msg",
            payload = new { type, message },
        });

    private static string Modern(string type, string text, string timestamp) =>
        JsonSerializer.Serialize(new
        {
            timestamp,
            type = "event_msg",
            payload = new
            {
                type = "item_completed",
                item = new { type, content = new[] { new { type = "Text", text } } },
            },
        });
}
