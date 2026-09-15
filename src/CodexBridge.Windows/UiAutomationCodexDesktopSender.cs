using System.Diagnostics;
using CodexBridge.Core;

namespace CodexBridge.Windows;

// The legacy public type name is retained for Host compatibility. Activation and
// queueing both use the stable thread UUID; titles are display-only.
public sealed class UiAutomationCodexDesktopSender : ICodexDesktopSender
{
    private readonly IThreadCatalog _catalog;
    private readonly TargetPolicy _policy;
    private readonly ICodexThreadActivator _threadActivator;
    private readonly ICodexQueueClient _queueClient;

    public UiAutomationCodexDesktopSender(IThreadCatalog catalog, TargetPolicy policy)
        : this(catalog, policy, new CodexProtocolThreadActivator(), new CodexCliQueueClient())
    {
    }

    public UiAutomationCodexDesktopSender(
        IThreadCatalog catalog,
        TargetPolicy policy,
        ICodexQueueClient queueClient)
        : this(catalog, policy, new CodexProtocolThreadActivator(), queueClient)
    {
    }

    public UiAutomationCodexDesktopSender(
        IThreadCatalog catalog,
        TargetPolicy policy,
        ICodexThreadActivator threadActivator,
        ICodexQueueClient queueClient)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _threadActivator = threadActivator ?? throw new ArgumentNullException(nameof(threadActivator));
        _queueClient = queueClient ?? throw new ArgumentNullException(nameof(queueClient));
    }

    public async Task<SendResult> SendAsync(
        string projectPath,
        string threadId,
        string message,
        CancellationToken cancellationToken = default)
    {
        ValidateMessage(message);
        var normalizedProject = _policy.AssertCanSend(projectPath, threadId);
        var thread = await _catalog.GetAsync(threadId, cancellationToken)
            ?? throw new InvalidOperationException($"找不到未归档会话：{threadId}");

        if (!string.Equals(TargetPolicy.NormalizePath(thread.Cwd), normalizedProject, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("会话项目路径与发送目标不一致。");

        await _threadActivator.ActivateAsync(thread.Id, cancellationToken);
        await _queueClient.QueueAsync(thread.Id, message, cancellationToken);
        var displayName = string.IsNullOrWhiteSpace(thread.DisplayName) ? thread.Title : thread.DisplayName;
        return new SendResult(thread.Id, displayName, false);
    }

    private static void ValidateMessage(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Length > 4_000)
            throw new ArgumentOutOfRangeException(nameof(message), "单条消息不得超过 4000 个字符。");
        if (message.Contains('\r') || message.Contains('\n'))
            throw new ArgumentException("仅允许发送单行纯文本。", nameof(message));
    }
}

public interface ICodexQueueClient
{
    Task QueueAsync(string threadId, string message, CancellationToken cancellationToken);
}

internal sealed class CodexCliQueueClient : ICodexQueueClient
{
    private static readonly TimeSpan QueueTimeout = TimeSpan.FromSeconds(30);

    public async Task QueueAsync(string threadId, string message, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FindCodexExecutable(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("queue");
        startInfo.ArgumentList.Add("--thread");
        startInfo.ArgumentList.Add(threadId);
        startInfo.ArgumentList.Add("--message");
        startInfo.ArgumentList.Add(message);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new DesktopVersionUnsupportedException("无法启动本机 Codex 队列服务。");

        var errorTask = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(QueueTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new DesktopVersionUnsupportedException("Codex 队列服务响应超时。");
        }

        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new DesktopVersionUnsupportedException(ToSafeError(error));
    }

    private static string FindCodexExecutable()
    {
        var binDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI", "Codex", "bin");
        var executable = Directory.Exists(binDirectory)
            ? Directory.EnumerateFiles(binDirectory, "codex.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        return executable ?? throw new DesktopVersionUnsupportedException("未找到本机 Codex 队列服务。请安装或更新 Codex Desktop。");
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    private static string ToSafeError(string error) =>
        error.Contains("no rollout found", StringComparison.OrdinalIgnoreCase)
            ? "该会话已不在本机 Codex 中，无法投递消息。"
            : "Codex 队列服务拒绝了此消息。请确认 Codex Desktop 已启动并更新到最新版。";
}
