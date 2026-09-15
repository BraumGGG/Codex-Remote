namespace CodexBridge.Windows.Tests;

public sealed class CodexDesktopLocatorTests
{
    private static readonly WindowIdentity ValidWindow = new(
        100,
        "ChatGPT",
        @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0_x64__publisher\app\ChatGPT.exe",
        "Codex",
        1234);

    [Fact]
    public void SelectUniqueCandidate_ReturnsSingleCodexWindow()
    {
        var result = CodexDesktopLocator.SelectUniqueCandidate(
        [
            ValidWindow,
            new WindowIdentity(101, "notepad", @"C:\Windows\notepad.exe", "Notes", 2345),
        ]);

        Assert.Equal(ValidWindow, result);
    }

    [Fact]
    public void SelectUniqueCandidate_RejectsNoMatch()
    {
        Assert.Throws<DesktopUnavailableException>(
            () => CodexDesktopLocator.SelectUniqueCandidate([]));
    }

    [Fact]
    public void SelectUniqueCandidate_RejectsMultipleMatches()
    {
        var another = ValidWindow with { ProcessId = 102, NativeWindowHandle = 3456 };

        Assert.Throws<DesktopVersionUnsupportedException>(
            () => CodexDesktopLocator.SelectUniqueCandidate([ValidWindow, another]));
    }

    [Fact]
    public void SelectUniqueCandidate_DeduplicatesSameNativeHandle()
    {
        var duplicate = ValidWindow with { Name = "Codex duplicate" };

        var result = CodexDesktopLocator.SelectUniqueCandidate([ValidWindow, duplicate]);

        Assert.Equal(ValidWindow.NativeWindowHandle, result.NativeWindowHandle);
    }
}
