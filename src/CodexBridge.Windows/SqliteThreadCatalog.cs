using CodexBridge.Core;
using Microsoft.Data.Sqlite;

namespace CodexBridge.Windows;

public sealed class SqliteThreadCatalog : IThreadCatalog
{
    private readonly string _connectionString;
    private readonly TargetPolicy _policy;

    public SqliteThreadCatalog(string databasePath, TargetPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _connectionString = BuildConnectionString(databasePath);
    }

    public static string BuildConnectionString(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 5,
        };

        return builder.ToString();
    }

    public async Task<IReadOnlyList<ThreadSummary>> ListByProjectAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        var normalizedProjectPath = _policy.AssertCanView(projectPath);
        var threads = new List<ThreadSummary>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, cwd, preview, updated_at_ms, archived, rollout_path, model_provider,
                   COALESCE(NULLIF(TRIM(name), ''), title) AS display_name
            FROM threads
            WHERE archived = 0
              AND source = 'vscode'
              AND thread_source = 'user'
              AND (
                    cwd = $projectPath COLLATE NOCASE
                    OR cwd = ($projectPath || '\') COLLATE NOCASE
                    OR cwd LIKE ($projectPath || '\%') COLLATE NOCASE
                    OR cwd = ('\\?\' || $projectPath) COLLATE NOCASE
                    OR cwd = ('\\?\' || $projectPath || '\') COLLATE NOCASE
                    OR cwd LIKE ('\\?\' || $projectPath || '\%') COLLATE NOCASE
                  )
            ORDER BY recency_at_ms DESC, updated_at_ms DESC
            """;
        command.Parameters.AddWithValue("$projectPath", normalizedProjectPath);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var thread = ReadThread(reader);
            var normalizedThreadPath = TargetPolicy.NormalizePath(thread.Cwd);
            if (IsSameOrDescendant(normalizedThreadPath, normalizedProjectPath))
            {
                threads.Add(thread with { Cwd = normalizedThreadPath });
            }
        }

        return threads;
    }

    private static bool IsSameOrDescendant(string path, string projectPath)
    {
        return string.Equals(path, projectPath, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(projectPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ThreadSummary?> GetAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, cwd, preview, updated_at_ms, archived, rollout_path, model_provider,
                   COALESCE(NULLIF(TRIM(name), ''), title) AS display_name
            FROM threads
            WHERE id = $threadId AND archived = 0
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$threadId", threadId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var thread = ReadThread(reader);
        // Reading a conversation is permitted for every authorized project.
        // Sending performs its stricter AssertCanSend check in the submission
        // path, so a read-only project must not make its history disappear.
        var normalizedPath = _policy.AssertCanView(thread.Cwd);
        return thread with { Cwd = normalizedPath };
    }

    private static ThreadSummary ReadThread(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt64(4),
        reader.GetInt64(5) != 0,
        reader.GetString(6),
        reader.GetString(7),
        reader.IsDBNull(8) ? reader.GetString(1) : reader.GetString(8));
}
