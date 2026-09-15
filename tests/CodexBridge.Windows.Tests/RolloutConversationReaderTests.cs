using System.Text;
using System.Text.Json;
using CodexBridge.Core;

namespace CodexBridge.Windows.Tests;

public sealed class RolloutConversationReaderTests
{
    [Fact]
    public void ParseVisibleEvent_IgnoresResponseItemsWithHiddenContext()
    {
        const string json = """
            {"timestamp":"2026-08-16T00:00:00Z","type":"response_item","payload":{"type":"message","role":"developer","content":[{"type":"input_text","text":"hidden"}]}}
            """;

        var result = RolloutConversationReader.ParseVisibleEvent(json);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("user_message", ConversationEventKind.UserMessage, "用户消息")]
    [InlineData("agent_message", ConversationEventKind.AgentMessage, "助手消息")]
    [InlineData("task_started", ConversationEventKind.TaskStarted, null)]
    [InlineData("task_complete", ConversationEventKind.TaskCompleted, null)]
    [InlineData("token_count", ConversationEventKind.TokenCount, null)]
    public void ParseVisibleEvent_MapsSupportedEvents(
        string type,
        ConversationEventKind expectedKind,
        string? message)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["turn_id"] = "turn-1",
        };
        if (message is not null)
        {
            payload["message"] = message;
        }

        var json = JsonSerializer.Serialize(new
        {
            timestamp = "2026-08-16T00:00:00Z",
            type = "event_msg",
            payload,
        });

        var result = RolloutConversationReader.ParseVisibleEvent(json);

        Assert.NotNull(result);
        Assert.Equal(expectedKind, result.Kind);
        Assert.Equal(message, result.Text);
        Assert.Equal("turn-1", result.TurnId);
    }

    [Fact]
    public void ParseVisibleEvent_ExtractsLocalImagesAndRedactsTheirPaths()
    {
        const string imagePath = @"C:\Users\Tester\AppData\Local\Temp\phone-test.png";
        var json = JsonSerializer.Serialize(new
        {
            timestamp = "2026-08-16T00:00:00Z",
            type = "event_msg",
            payload = new
            {
                type = "user_message",
                message = $"\n# Files mentioned by the user:\n\n{imagePath}\n\n## My request:\n查看图片",
                local_images = new[] { imagePath },
            },
        });

        var result = RolloutConversationReader.ParseVisibleEvent(json);

        Assert.NotNull(result);
        Assert.Equal([imagePath], result.LocalImagePaths);
        Assert.DoesNotContain(imagePath, result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Files mentioned", result.Text, StringComparison.Ordinal);
        Assert.Equal("查看图片", result.Text);
    }

    [Fact]
    public void ParseVisibleEvent_MapsModernCompletedUserAndAgentItems()
    {
        const string json = """
            {"timestamp":"2026-08-16T00:00:00Z","type":"event_msg","payload":{"type":"item_completed","item":{"type":"AgentMessage","id":"msg-1","content":[{"type":"Text","text":"历史回复"}],"phase":"final"}}}
            """;

        var result = RolloutConversationReader.ParseVisibleEvent(json);

        Assert.NotNull(result);
        Assert.Equal(ConversationEventKind.AgentMessage, result.Kind);
        Assert.Equal("历史回复", result.Text);
    }

    [Fact]
    public void ParseVisibleEvent_RedactsSlashVariantOutsideGeneratedAttachmentHeader()
    {
        const string imagePath = @"C:\Users\Tester\Temp\phone-test.png";
        var slashPath = imagePath.Replace('\\', '/');
        var json = JsonSerializer.Serialize(new
        {
            type = "event_msg",
            payload = new
            {
                type = "user_message",
                message = $"图片位置：{slashPath}",
                local_images = new[] { imagePath },
            },
        });

        var result = RolloutConversationReader.ParseVisibleEvent(json);

        Assert.NotNull(result);
        Assert.Equal("图片位置：[图片]", result.Text);
    }

    [Fact]
    public async Task ReadAsync_SkipsBlankMalformedAndHiddenLines()
    {
        var directory = TestPaths.CreateDirectory("rollout-read");
        var path = Path.Combine(directory, "rollout.jsonl");
        try
        {
            await File.WriteAllTextAsync(
                path,
                """

                {not-json}
                {"type":"response_item","payload":{"type":"message","role":"developer"}}
                {"timestamp":"2026-08-16T00:00:00Z","type":"event_msg","payload":{"type":"user_message","message":"你好"}}
                """,
                Encoding.UTF8);

            var events = new List<ConversationEvent>();
            await foreach (var item in new RolloutConversationReader().ReadAsync(path, follow: false))
            {
                events.Add(item);
            }

            var visible = Assert.Single(events);
            Assert.Equal(ConversationEventKind.UserMessage, visible.Kind);
            Assert.Equal("你好", visible.Text);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_FollowYieldsLineAppendedAfterEndOfFile()
    {
        var directory = TestPaths.CreateDirectory("rollout-follow");
        var path = Path.Combine(directory, "rollout.jsonl");
        await File.WriteAllTextAsync(path, VisibleEvent("user_message", "first") + Environment.NewLine);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var enumerator = new RolloutConversationReader()
            .ReadAsync(path, follow: true, cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        try
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal("first", enumerator.Current.Text);

            await File.AppendAllTextAsync(
                path,
                VisibleEvent("agent_message", "appended") + Environment.NewLine,
                cancellation.Token);

            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(ConversationEventKind.AgentMessage, enumerator.Current.Kind);
            Assert.Equal("appended", enumerator.Current.Text);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string VisibleEvent(string type, string message) =>
        JsonSerializer.Serialize(new
        {
            timestamp = "2026-08-16T00:00:00Z",
            type = "event_msg",
            payload = new { type, message },
        });
}
