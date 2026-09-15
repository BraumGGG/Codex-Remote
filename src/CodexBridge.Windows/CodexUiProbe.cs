using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace CodexBridge.Windows;

public sealed class CodexUiProbe
{
    private static readonly string[] TargetNames =
    [
        "测试会话",
        "返回测试会话1",
        "返回测试会话2",
    ];

    private static readonly HashSet<ControlType> IncludedControlTypes =
    [
        ControlType.Text,
        ControlType.Edit,
        ControlType.Document,
        ControlType.Button,
        ControlType.TreeItem,
        ControlType.ListItem,
        ControlType.Pane,
    ];

    public UiTreeSnapshot Capture()
    {
        using var automation = new UIA3Automation();
        var window = new CodexDesktopLocator().FindUniqueWindow(automation);
        var identity = CreateWindowIdentity(window);
        var elements = window
            .FindAllDescendants()
            .Where(element => IncludedControlTypes.Contains(element.ControlType))
            .Take(2_000)
            .Select((element, index) => CreateSnapshot(element, index))
            .ToArray();
        var globalMatches = FindGlobalMatches(automation);
        var nativeChildren = NativeWindowEnumerator.EnumerateDescendants(
            new nint(identity.NativeWindowHandle));
        var fragmentElements = FindFragmentElements(automation, nativeChildren);

        return new UiTreeSnapshot(identity, elements, globalMatches, nativeChildren, fragmentElements);
    }

    private static IReadOnlyList<UiElementSnapshot> FindFragmentElements(
        UIA3Automation automation,
        IReadOnlyList<NativeWindowInfo> nativeChildren)
    {
        var elements = new List<AutomationElement>();

        foreach (var child in nativeChildren.Where(child =>
                     child.ClassName.Contains("Chrome", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var root = automation.FromHandle(new nint(child.Handle));
                elements.Add(root);
                elements.AddRange(root.FindAllDescendants());
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Some Chromium helper HWNDs do not expose an accessibility fragment.
            }
        }

        return elements
            .Where(element => IncludedControlTypes.Contains(element.ControlType))
            .DistinctBy(element => (
                element.Properties.ProcessId.ValueOrDefault,
                element.Properties.NativeWindowHandle.ValueOrDefault,
                element.ControlType,
                element.Properties.Name.ValueOrDefault,
                element.Properties.BoundingRectangle.ValueOrDefault))
            .Take(2_000)
            .Select((element, index) => CreateSnapshot(element, index))
            .ToArray();
    }

    private static IReadOnlyList<UiElementSnapshot> FindGlobalMatches(UIA3Automation automation)
    {
        var desktop = automation.GetDesktop();
        var matches = new List<AutomationElement>();

        foreach (var name in TargetNames)
        {
            matches.AddRange(desktop.FindAllDescendants(condition => condition.ByName(name)));
        }

        matches.AddRange(desktop.FindAllDescendants(condition => condition.ByControlType(ControlType.Edit)));
        matches.AddRange(desktop.FindAllDescendants(condition => condition.ByControlType(ControlType.Document)));

        return matches
            .Where(IsChatGptElement)
            .DistinctBy(element => (
                element.Properties.ProcessId.ValueOrDefault,
                element.Properties.NativeWindowHandle.ValueOrDefault,
                element.ControlType,
                element.Properties.Name.ValueOrDefault,
                element.Properties.BoundingRectangle.ValueOrDefault))
            .Take(500)
            .Select((element, index) => CreateSnapshot(element, index))
            .ToArray();
    }

    private static bool IsChatGptElement(AutomationElement element)
    {
        var processId = element.Properties.ProcessId.ValueOrDefault;
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return string.Equals(process.ProcessName, "ChatGPT", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static WindowIdentity CreateWindowIdentity(AutomationElement window)
    {
        var processId = window.Properties.ProcessId.ValueOrDefault;
        using var process = Process.GetProcessById(processId);
        return new WindowIdentity(
            processId,
            process.ProcessName,
            process.MainModule?.FileName,
            window.Properties.Name.ValueOrDefault ?? string.Empty,
            window.Properties.NativeWindowHandle.ValueOrDefault.ToInt64());
    }

    private static UiElementSnapshot CreateSnapshot(AutomationElement element, int index)
    {
        var rectangle = element.Properties.BoundingRectangle.ValueOrDefault;
        return new UiElementSnapshot(
            index,
            element.ControlType.ToString(),
            element.Properties.Name.ValueOrDefault ?? string.Empty,
            element.Properties.AutomationId.ValueOrDefault ?? string.Empty,
            element.Properties.ClassName.ValueOrDefault ?? string.Empty,
            element.Properties.IsEnabled.ValueOrDefault,
            element.Properties.IsOffscreen.ValueOrDefault,
            rectangle.Left,
            rectangle.Top,
            rectangle.Width,
            rectangle.Height);
    }
}
