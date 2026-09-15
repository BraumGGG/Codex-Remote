using System.Net;
using System.Text.Json;
using CodexBridge.Core;
using Microsoft.Data.Sqlite;

namespace CodexBridge.Host.Tests;

public sealed class WorkspaceApiTests
{
    internal const string ThreadOne = "01a00749-2fb0-7fa0-9186-4f8292732f2c";
    internal const string ThreadTwo = "01a00749-6d7c-7072-9b22-f4a70ea35331";
    internal const string ThirdThread = "01a00748-fa69-7e13-8800-74eadbd62cf7";

    [Fact]
    public async Task LegacyMobileHttpApis_AreNotExposed()
    {
        await using var host = await RunningHost.StartAsync();
        foreach (var path in new[]
                 {
                     "/api/pair", "/api/capabilities", "/api/projects",
                     "/api/remote/pairing",
                     $"/api/projects/project/threads", $"/api/threads/{ThreadOne}/events",
                     $"/api/threads/{ThreadOne}/messages",
                 })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync(path)).StatusCode);
        }
    }

    internal static void CreateWorkspaceData(string directory) =>
        CreateWorkspaceData(directory, TargetPolicy.AllowedProjectPath);

    internal static void CreateWorkspaceData(string directory, string projectPath)
    {
        var firstRollout = Path.Combine(directory, "one.jsonl");
        var secondRollout = Path.Combine(directory, "two.jsonl");
        var imagePath = Path.Combine(directory, "attachment.png");
        var nonImagePath = Path.Combine(directory, "private.txt");
        File.WriteAllBytes(imagePath,
        [
            0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
            0x00, 0x00, 0x00, 0x00,
        ]);
        File.WriteAllText(nonImagePath, "must not be exposed");
        File.WriteAllLines(firstRollout,
        [
            Event(
                "user_message",
                $"来自手机\n{imagePath}\n{nonImagePath}",
                localImages: [imagePath, nonImagePath]),
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-08-16T08:00:01Z",
                type = "response_item",
                payload = new { type = "developer_message", message = "hidden" },
            }),
            Event("agent_message", "已收到"),
        ]);
        File.WriteAllLines(secondRollout,
        [
            Event("task_started", null, "turn-2"),
            Event("task_complete", null, "turn-2"),
        ]);

        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(directory, "state.sqlite")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE threads (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                cwd TEXT NOT NULL,
                preview TEXT NOT NULL,
                updated_at_ms INTEGER NOT NULL,
                archived INTEGER NOT NULL,
                rollout_path TEXT NOT NULL,
                model_provider TEXT NOT NULL,
                recency_at_ms INTEGER NOT NULL
            );
            INSERT INTO threads VALUES
              ($one, '返回测试会话1', $cwd, '预览1', 100, 0, $rolloutOne, 'custom', 100),
              ($two, '返回测试会话2', $cwd, '预览2', 200, 0, $rolloutTwo, 'custom', 200),
              ($third, '返回未授权会话', $thirdCwd, '第三个', 300, 0, $rolloutOne, 'custom', 300);
            """;
        command.Parameters.AddWithValue("$one", ThreadOne);
        command.Parameters.AddWithValue("$two", ThreadTwo);
        command.Parameters.AddWithValue("$third", ThirdThread);
        command.Parameters.AddWithValue("$cwd", projectPath);
        command.Parameters.AddWithValue("$thirdCwd", projectPath + "-未授权");
        command.Parameters.AddWithValue("$rolloutOne", firstRollout);
        command.Parameters.AddWithValue("$rolloutTwo", secondRollout);
        command.ExecuteNonQuery();
    }

    private static string Event(
        string type,
        string? message,
        string? turnId = null,
        string[]? localImages = null) =>
        JsonSerializer.Serialize(new
        {
            timestamp = "2026-08-16T08:00:00Z",
            type = "event_msg",
            payload = new
            {
                type,
                message,
                turn_id = turnId,
                local_images = localImages,
            },
        });
}
