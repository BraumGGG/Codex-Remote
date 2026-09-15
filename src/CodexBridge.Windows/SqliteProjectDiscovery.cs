using CodexBridge.Core;
using Microsoft.Data.Sqlite;

namespace CodexBridge.Windows;

public sealed class SqliteProjectDiscovery : IProjectDiscovery
{
    private readonly string _connectionString;

    public SqliteProjectDiscovery(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
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

    public async Task<IReadOnlyList<DiscoveredProject>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var projects = new Dictionary<string, ProjectAccumulator>(StringComparer.OrdinalIgnoreCase);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cwd, updated_at_ms
            FROM threads
            WHERE archived = 0
              AND source = 'vscode'
              AND thread_source = 'user'
            ORDER BY recency_at_ms DESC, updated_at_ms DESC
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string path;
            try
            {
                path = TargetPolicy.NormalizePath(reader.GetString(0));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                continue;
            }

            var updatedAtMs = reader.GetInt64(1);
            if (projects.TryGetValue(path, out var current))
            {
                projects[path] = current with
                {
                    ThreadCount = current.ThreadCount + 1,
                    UpdatedAtMs = Math.Max(current.UpdatedAtMs, updatedAtMs),
                };
            }
            else
            {
                projects.Add(path, new ProjectAccumulator(1, updatedAtMs));
            }
        }

        return projects
            .Select(pair => new DiscoveredProject(
                ProjectName(pair.Key),
                pair.Key,
                pair.Value.ThreadCount,
                pair.Value.UpdatedAtMs))
            .OrderByDescending(project => project.UpdatedAtMs)
            .ThenBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ProjectName(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private sealed record ProjectAccumulator(int ThreadCount, long UpdatedAtMs);
}
