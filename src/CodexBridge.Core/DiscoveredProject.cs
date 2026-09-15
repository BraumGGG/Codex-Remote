namespace CodexBridge.Core;

public sealed record DiscoveredProject(
    string Name,
    string Path,
    int ThreadCount,
    long UpdatedAtMs);

public interface IProjectDiscovery
{
    Task<IReadOnlyList<DiscoveredProject>> ListAsync(
        CancellationToken cancellationToken = default);
}
