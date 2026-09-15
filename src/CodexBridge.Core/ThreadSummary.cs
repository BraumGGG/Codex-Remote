namespace CodexBridge.Core;

public sealed record ThreadSummary(
    string Id,
    string Title,
    string Cwd,
    string Preview,
    long UpdatedAtMs,
    bool Archived,
    string RolloutPath,
    string ModelProvider,
    string DisplayName = "");
