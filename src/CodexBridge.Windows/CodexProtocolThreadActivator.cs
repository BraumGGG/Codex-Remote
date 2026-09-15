using System.ComponentModel;
using System.Diagnostics;

namespace CodexBridge.Windows;

public interface ICodexThreadActivator
{
    Task ActivateAsync(string threadId, CancellationToken cancellationToken);
}

internal sealed class CodexProtocolThreadActivator : ICodexThreadActivator
{
    private static readonly TimeSpan DefaultNavigationDelay = TimeSpan.FromMilliseconds(500);

    private readonly Func<Uri, bool> _launchProtocol;
    private readonly TimeSpan _navigationDelay;

    public CodexProtocolThreadActivator()
        : this(LaunchProtocol, DefaultNavigationDelay)
    {
    }

    internal CodexProtocolThreadActivator(Func<Uri, bool> launchProtocol, TimeSpan navigationDelay)
    {
        _launchProtocol = launchProtocol ?? throw new ArgumentNullException(nameof(launchProtocol));
        if (navigationDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(navigationDelay));
        _navigationDelay = navigationDelay;
    }

    public async Task ActivateAsync(string threadId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(threadId, "D", out var parsedThreadId))
            throw new DesktopVersionUnsupportedException("会话标识不是有效的 UUID，无法激活 Codex 会话。");

        var threadUri = new Uri($"codex://threads/{parsedThreadId:D}");
        try
        {
            if (!_launchProtocol(threadUri))
                throw new DesktopUnavailableException("Windows 未能打开 Codex Desktop 会话。");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new DesktopUnavailableException("无法通过 Codex Desktop 协议激活目标会话。");
        }

        if (_navigationDelay > TimeSpan.Zero)
            await Task.Delay(_navigationDelay, cancellationToken);
    }

    private static bool LaunchProtocol(Uri threadUri)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = threadUri.AbsoluteUri,
            UseShellExecute = true,
        });
        return true;
    }
}
