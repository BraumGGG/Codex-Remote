using CodexBridge.Core;
using Microsoft.Data.Sqlite;

namespace CodexBridge.Windows.Tests;

public sealed class SqliteProjectDiscoveryTests : IDisposable
{
    private readonly string _directory = TestPaths.CreateDirectory("sqlite-project-discovery");
    private readonly string _databasePath;

    public SqliteProjectDiscoveryTests()
    {
        _databasePath = Path.Combine(_directory, "state.sqlite");
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE threads (
                id TEXT PRIMARY KEY,
                cwd TEXT NOT NULL,
                updated_at_ms INTEGER NOT NULL,
                archived INTEGER NOT NULL,
                source TEXT NOT NULL,
                thread_source TEXT NOT NULL,
                recency_at_ms INTEGER NOT NULL
            );
            INSERT INTO threads VALUES
              ('one', 'D:\projects\alpha', 100, 0, 'vscode', 'user', 100),
              ('two', '\\?\D:\projects\alpha', 300, 0, 'vscode', 'user', 300),
              ('three', 'D:\other\alpha', 200, 0, 'vscode', 'user', 200),
              ('cli', 'D:\projects\cli', 600, 0, 'exec', 'user', 600),
              ('subagent', 'D:\projects\subagent', 700, 0, 'vscode', 'subagent', 700),
              ('archived', 'D:\projects\hidden', 500, 1, 'vscode', 'user', 500);
            """;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void ConnectionString_IsStrictlyReadOnly()
    {
        var builder = new SqliteConnectionStringBuilder(
            SqliteProjectDiscovery.BuildConnectionString(_databasePath));

        Assert.Equal(SqliteOpenMode.ReadOnly, builder.Mode);
    }

    [Fact]
    public async Task ListAsync_GroupsActiveThreadsByNormalizedPath()
    {
        var projects = await new SqliteProjectDiscovery(_databasePath).ListAsync();

        Assert.Equal(2, projects.Count);
        Assert.Equal(TargetPolicy.NormalizePath(@"D:\projects\alpha"), projects[0].Path);
        Assert.Equal(2, projects[0].ThreadCount);
        Assert.Equal(300, projects[0].UpdatedAtMs);
        Assert.Equal("alpha", projects[0].Name);
        Assert.Equal(TargetPolicy.NormalizePath(@"D:\other\alpha"), projects[1].Path);
        Assert.DoesNotContain(projects, project => project.Path.Contains("hidden", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
