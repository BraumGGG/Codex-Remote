using CodexBridge.Windows;

namespace CodexBridge.Windows.Tests;

public sealed class CodexProtocolThreadActivatorTests
{
    private const string ThreadId = "01a00749-6d7c-7072-9b22-f4a70ea35331";

    [Fact]
    public async Task ActivateAsync_OpensCodexThreadUriWithNormalizedUuid()
    {
        Uri? launchedUri = null;
        var activator = new CodexProtocolThreadActivator(
            uri =>
            {
                launchedUri = uri;
                return true;
            },
            TimeSpan.Zero);

        await activator.ActivateAsync(ThreadId.ToUpperInvariant(), CancellationToken.None);

        Assert.Equal($"codex://threads/{ThreadId}", launchedUri?.AbsoluteUri);
    }

    [Fact]
    public async Task ActivateAsync_RejectsInvalidThreadIdBeforeLaunching()
    {
        var launchCount = 0;
        var activator = new CodexProtocolThreadActivator(
            _ =>
            {
                launchCount++;
                return true;
            },
            TimeSpan.Zero);

        await Assert.ThrowsAsync<DesktopVersionUnsupportedException>(
            () => activator.ActivateAsync("not-a-uuid", CancellationToken.None));

        Assert.Equal(0, launchCount);
    }

    [Fact]
    public async Task ActivateAsync_FailsWhenWindowsDoesNotAcceptProtocolLaunch()
    {
        var activator = new CodexProtocolThreadActivator(_ => false, TimeSpan.Zero);

        await Assert.ThrowsAsync<DesktopUnavailableException>(
            () => activator.ActivateAsync(ThreadId, CancellationToken.None));
    }
}
