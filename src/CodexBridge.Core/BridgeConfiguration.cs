using System.Text.Json.Serialization;

namespace CodexBridge.Core;

public sealed record AuthorizedProject(string Path, bool CanSend = true);

public sealed record BridgeConfiguration
{
    public const int CurrentVersion = 1;

    [JsonConstructor]
    public BridgeConfiguration(int version, IReadOnlyList<AuthorizedProject>? projects)
    {
        Version = version;
        Projects = projects ?? [];
    }

    public int Version { get; }
    public IReadOnlyList<AuthorizedProject> Projects { get; }
}
