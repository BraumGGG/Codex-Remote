using System.Text.Json;

namespace CodexBridge.Core;

public sealed class BridgeConfigurationStore
{
    public const int MaximumProjects = 32;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string _path;

    public BridgeConfigurationStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public BridgeConfiguration LoadOrCreate()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                var empty = new BridgeConfiguration(BridgeConfiguration.CurrentVersion, []);
                Persist(empty);
                return empty;
            }

            try
            {
                var stored = JsonSerializer.Deserialize<BridgeConfiguration>(
                    File.ReadAllText(_path),
                    JsonOptions) ?? throw new InvalidDataException("Bridge configuration is empty.");
                return NormalizeAndValidate(stored);
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException("Bridge configuration is invalid.", exception);
            }
        }
    }

    public void Save(BridgeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        lock (_gate)
        {
            Persist(NormalizeAndValidate(configuration));
        }
    }

    private void Persist(BridgeConfiguration configuration)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Bridge configuration path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(configuration, JsonOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static BridgeConfiguration NormalizeAndValidate(BridgeConfiguration configuration)
    {
        if (configuration.Version != BridgeConfiguration.CurrentVersion)
            throw new InvalidDataException("Bridge configuration version is unsupported.");
        if (configuration.Projects.Count > MaximumProjects)
            throw new InvalidDataException($"At most {MaximumProjects} projects may be authorized.");

        var normalized = new List<AuthorizedProject>(configuration.Projects.Count);
        foreach (var project in configuration.Projects)
        {
            if (project is null || string.IsNullOrWhiteSpace(project.Path) ||
                !Path.IsPathFullyQualified(project.Path))
                throw new InvalidDataException("Authorized project path must be absolute.");

            string path;
            try
            {
                path = TargetPolicy.NormalizePath(project.Path);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new InvalidDataException("Authorized project path is invalid.", exception);
            }

            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root) ||
                string.Equals(path, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A filesystem root cannot be authorized as a project.");

            if (normalized.Any(existing => PathsOverlap(existing.Path, path)))
                throw new InvalidDataException("Authorized project paths must be distinct and non-overlapping.");
            normalized.Add(new AuthorizedProject(path, project.CanSend));
        }

        return new BridgeConfiguration(configuration.Version, normalized);
    }

    private static bool PathsOverlap(string first, string second)
    {
        if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase)) return true;
        return IsDescendant(first, second) || IsDescendant(second, first);
    }

    private static bool IsDescendant(string parent, string candidate)
    {
        var prefix = Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
