namespace CodexBridge.Core;

public sealed class TargetPolicy
{
    public const string AllowedProjectPath = @"D:\claudecode\cchaha\Project\测试会话";

    private IReadOnlyList<string> _allowedProjectPaths;
    private IReadOnlyDictionary<string, bool> _projectSendPermissions;

    public TargetPolicy()
        : this([AllowedProjectPath])
    {
    }

    public TargetPolicy(IEnumerable<string> authorizedProjectPaths)
    {
        ArgumentNullException.ThrowIfNull(authorizedProjectPaths);
        _allowedProjectPaths = NormalizePaths(authorizedProjectPaths);
        _projectSendPermissions = _allowedProjectPaths.ToDictionary(path => path, _ => true, StringComparer.OrdinalIgnoreCase);
    }

    public TargetPolicy(IEnumerable<AuthorizedProject> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var normalized = projects.Select(project => (Path: NormalizePath(project.Path), project.CanSend)).ToArray();
        _allowedProjectPaths = normalized.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _projectSendPermissions = normalized
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().CanSend, StringComparer.OrdinalIgnoreCase);
    }

    public void ReplaceAllowedProjectPaths(IEnumerable<string> authorizedProjectPaths)
    {
        ArgumentNullException.ThrowIfNull(authorizedProjectPaths);
        Interlocked.Exchange(ref _allowedProjectPaths, NormalizePaths(authorizedProjectPaths));
        Interlocked.Exchange(ref _projectSendPermissions, _allowedProjectPaths.ToDictionary(path => path, _ => true, StringComparer.OrdinalIgnoreCase));
    }

    public void ReplaceAuthorizedProjects(IEnumerable<AuthorizedProject> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var normalized = projects.Select(project => (Path: NormalizePath(project.Path), project.CanSend)).ToArray();
        Interlocked.Exchange(ref _allowedProjectPaths, normalized.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        Interlocked.Exchange(ref _projectSendPermissions, normalized.GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().CanSend, StringComparer.OrdinalIgnoreCase));
    }

    public string AssertCanView(string projectPath)
    {
        var normalizedPath = NormalizePath(projectPath);
        if (!_allowedProjectPaths.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"Project is outside the validation allowlist: {normalizedPath}");
        }

        return normalizedPath;
    }

    public string AssertCanSend(string projectPath, string threadId)
    {
        var normalizedPath = AssertCanView(projectPath);
        if (string.IsNullOrWhiteSpace(threadId))
            throw new UnauthorizedAccessException("Thread identifier is invalid.");
        if (!_projectSendPermissions.TryGetValue(normalizedPath, out var canSend) || !canSend)
            throw new UnauthorizedAccessException("Project is read-only.");

        return normalizedPath;
    }

    public IReadOnlyList<string> GetAllowedProjectPaths() => _allowedProjectPaths;
    public bool CanSend(string projectPath) => _projectSendPermissions.TryGetValue(NormalizePath(projectPath), out var value) && value;

    private static IReadOnlyList<string> NormalizePaths(IEnumerable<string> paths) => paths
        .Select(NormalizePath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var withoutLongPathPrefix = path.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? path[4..]
            : path;

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(withoutLongPathPrefix));
    }
}
