namespace CodexBridge.Windows;

public interface ICodexDesktopSender
{
    Task<SendResult> SendAsync(
        string projectPath,
        string threadId,
        string message,
        CancellationToken cancellationToken = default);
}

public sealed record SendResult(string ThreadId, string ThreadTitle, bool RestoredFromMinimized);
