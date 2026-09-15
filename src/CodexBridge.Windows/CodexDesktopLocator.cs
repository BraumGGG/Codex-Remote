using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace CodexBridge.Windows;

public sealed class CodexDesktopLocator
{
    public AutomationElement FindUniqueWindow(UIA3Automation automation)
    {
        ArgumentNullException.ThrowIfNull(automation);

        var windows = automation
            .GetDesktop()
            .FindAllChildren(condition => condition.ByControlType(ControlType.Window));
        var candidates = windows
            .Select(element => (Element: element, Identity: CreateIdentity(element)))
            .Where(candidate => candidate.Identity is not null)
            .Select(candidate => (candidate.Element, Identity: candidate.Identity!))
            .ToArray();

        var selected = SelectUniqueCandidate(candidates.Select(candidate => candidate.Identity));
        return candidates.Single(candidate => candidate.Identity == selected).Element;
    }

    public static WindowIdentity SelectUniqueCandidate(IEnumerable<WindowIdentity> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var matches = candidates
            .Where(IsCodexWindow)
            .DistinctBy(candidate => candidate.NativeWindowHandle)
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new DesktopUnavailableException("未找到 Codex Desktop 顶层窗口。"),
            _ => throw new DesktopVersionUnsupportedException(
                $"找到多个 Codex Desktop 顶层窗口：{string.Join(", ", matches.Select(x => x.NativeWindowHandle))}"),
        };
    }

    private static bool IsCodexWindow(WindowIdentity candidate)
    {
        var processMatches = string.Equals(candidate.ProcessName, "ChatGPT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.ProcessName, "Codex", StringComparison.OrdinalIgnoreCase);
        if (!processMatches) return false;
        var executable = candidate.ExecutablePath ?? string.Empty;
        return executable.Contains("Codex", StringComparison.OrdinalIgnoreCase) ||
            executable.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase);
    }

    private static WindowIdentity? CreateIdentity(AutomationElement element)
    {
        var processId = element.Properties.ProcessId.ValueOrDefault;
        var nativeWindowHandle = element.Properties.NativeWindowHandle.ValueOrDefault;
        if (processId <= 0 || nativeWindowHandle == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return new WindowIdentity(
                processId,
                process.ProcessName,
                TryGetExecutablePath(process),
                element.Properties.Name.ValueOrDefault ?? string.Empty,
                nativeWindowHandle.ToInt64());
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
