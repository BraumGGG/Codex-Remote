using CodexBridge.Core;
using Microsoft.Data.Sqlite;

namespace CodexBridge.Windows.Tests;

public sealed class SqliteThreadCatalogTests : IDisposable
{
    private const string SecondProject = @"D:\claudecode\cchaha\Project\第二项目";
    private readonly string _directory = TestPaths.CreateDirectory("sqlite-catalog");
    private readonly string _databasePath;

    public SqliteThreadCatalogTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "state.sqlite");
        CreateDatabase();
    }

    [Fact]
    public void BuildConnectionString_IsReadOnly()
    {
        var result = SqliteThreadCatalog.BuildConnectionString(_databasePath);
        var builder = new SqliteConnectionStringBuilder(result);

        Assert.Equal(SqliteOpenMode.ReadOnly, builder.Mode);
    }

    [Fact]
    public async Task ListByProjectAsync_ReturnsEveryActiveThreadInAuthorizedProjectInRecencyOrder()
    {
        var catalog = new SqliteThreadCatalog(
            _databasePath,
            new TargetPolicy([TargetPolicy.AllowedProjectPath, SecondProject]));

        var result = await catalog.ListByProjectAsync(TargetPolicy.AllowedProjectPath);

        Assert.Equal(3, result.Count);
        Assert.Equal("01a00748-fa69-7e13-8800-74eadbd62cf7", result[0].Id);
        Assert.Equal("01a00749-6d7c-7072-9b22-f4a70ea35331", result[1].Id);
        Assert.Equal("01a00749-2fb0-7fa0-9186-4f8292732f2c", result[2].Id);
        Assert.All(result, thread => Assert.False(thread.Archived));

        var second = await catalog.ListByProjectAsync(SecondProject);
        var secondThread = Assert.Single(second);
        Assert.Equal("second-project", secondThread.Id);
        Assert.Equal("返回测试会话1", secondThread.Title);
    }

    [Fact]
    public async Task GetAsync_AuthorizesByStoredProjectAndRejectsOutsideProject()
    {
        var catalog = new SqliteThreadCatalog(
            _databasePath,
            new TargetPolicy([TargetPolicy.AllowedProjectPath, SecondProject]));

        Assert.NotNull(await catalog.GetAsync("01a00748-fa69-7e13-8800-74eadbd62cf7"));
        Assert.NotNull(await catalog.GetAsync("second-project"));
        Assert.Null(await catalog.GetAsync("missing-thread"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => catalog.GetAsync("outside-project"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => catalog.GetAsync("similar-project"));
    }

    [Fact]
    public async Task ReadOnlyProject_CanListAndReadHistoryButCannotSend()
    {
        var policy = new TargetPolicy([
            new AuthorizedProject(TargetPolicy.AllowedProjectPath, CanSend: false),
            new AuthorizedProject(SecondProject, CanSend: true),
        ]);
        var catalog = new SqliteThreadCatalog(_databasePath, policy);

        var threads = await catalog.ListByProjectAsync(TargetPolicy.AllowedProjectPath);

        Assert.Equal(3, threads.Count);
        Assert.NotNull(await catalog.GetAsync("01a00748-fa69-7e13-8800-74eadbd62cf7"));
        Assert.Throws<UnauthorizedAccessException>(() =>
            policy.AssertCanSend(TargetPolicy.AllowedProjectPath, "01a00748-fa69-7e13-8800-74eadbd62cf7"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private void CreateDatabase()
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
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
                name TEXT,
                source TEXT NOT NULL,
                thread_source TEXT NOT NULL,
                recency_at_ms INTEGER NOT NULL
            );

            INSERT INTO threads VALUES
              ('01a00749-2fb0-7fa0-9186-4f8292732f2c', '返回测试会话1', '\\?\D:\claudecode\cchaha\Project\测试会话', '测试1', 100, 0, 'one.jsonl', 'custom', '显示名1', 'vscode', 'user', 100),
              ('01a00749-6d7c-7072-9b22-f4a70ea35331', '返回测试会话2', 'D:\claudecode\cchaha\Project\测试会话', '测试2', 200, 0, 'two.jsonl', 'custom', '', 'vscode', 'user', 200),
              ('01a00748-fa69-7e13-8800-74eadbd62cf7', '未授权会话', 'D:\claudecode\cchaha\Project\测试会话', '第三个', 300, 0, 'three.jsonl', 'custom', '显示名3', 'vscode', 'user', 300),
              ('second-project', '返回测试会话1', 'D:\claudecode\cchaha\Project\第二项目', '同名但不同项目', 350, 0, 'second.jsonl', 'custom', '第二项目显示名', 'vscode', 'user', 350),
              ('outside-project', '其他项目', 'D:\claudecode\cchaha\Project\其他项目', '越权', 500, 0, 'outside.jsonl', 'custom', '其他项目', 'vscode', 'user', 500),
              ('similar-project', '相似路径', 'D:\claudecode\cchaha\Project\测试会话-copy', '越权', 450, 0, 'similar.jsonl', 'custom', '相似路径', 'vscode', 'user', 450),
              ('cli-thread', 'CLI 会话', 'D:\claudecode\cchaha\Project\测试会话', '应隐藏', 600, 0, 'cli.jsonl', 'custom', 'CLI 会话', 'exec', 'user', 600),
              ('subagent-thread', '子代理', 'D:\claudecode\cchaha\Project\测试会话', '应隐藏', 700, 0, 'subagent.jsonl', 'custom', '子代理', 'vscode', 'subagent', 700),
              ('01a00749-2fb0-7fa0-9186-4f8292732f2c-archived', '归档会话', 'D:\claudecode\cchaha\Project\测试会话', '归档', 400, 1, 'archive.jsonl', 'custom', '归档会话', 'vscode', 'user', 400);
            """;
        command.ExecuteNonQuery();
    }
}
