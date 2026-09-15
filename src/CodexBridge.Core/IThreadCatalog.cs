namespace CodexBridge.Core;

public interface IThreadCatalog
{
    Task<IReadOnlyList<ThreadSummary>> ListByProjectAsync(
        string projectPath,
        CancellationToken cancellationToken = default);

    Task<ThreadSummary?> GetAsync(
        string threadId,
        CancellationToken cancellationToken = default);
}
