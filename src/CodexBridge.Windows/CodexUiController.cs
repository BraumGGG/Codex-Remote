using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace CodexBridge.Windows;

public sealed class CodexUiController
{
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(5);

    public void SelectThread(string exactTitle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exactTitle);

        using var automation = new UIA3Automation();
        var window = new CodexDesktopLocator().FindUniqueWindow(automation);
        SelectThread(automation, window, exactTitle);
    }

    internal void SelectThread(
        UIA3Automation automation,
        AutomationElement window,
        string exactTitle)
    {
        ArgumentNullException.ThrowIfNull(automation);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(exactTitle);

        var webRoot = FindWebRoot(automation, window);
        var matches = FindThreadMatches(webRoot, exactTitle);
        if (matches.Length == 0)
        {
            SearchForThread(automation, window, exactTitle);
            webRoot = FindWebRoot(automation, window);
            matches = FindThreadMatches(webRoot, exactTitle);
        }

        var button = matches.Length switch
        {
            1 => matches[0].AsButton(),
            0 => throw new DesktopVersionUnsupportedException(
                $"没有找到唯一会话按钮：{exactTitle}。可见候选：{DescribeThreadCandidates(webRoot)}"),
            _ => throw new DesktopVersionUnsupportedException($"会话按钮不唯一，已停止：{exactTitle}"),
        };

        button.Invoke();
        WaitUntilNavigationCompletes(automation, window, exactTitle);
    }

    private static AutomationElement[] FindThreadMatches(AutomationElement webRoot, string exactTitle) =>
        webRoot
            .FindAllDescendants()
            .Where(IsThreadButton)
            .Where(element => SameThreadName(NameOf(element), exactTitle))
            .ToArray();

    private static void SearchForThread(
        UIA3Automation automation,
        AutomationElement window,
        string exactTitle)
    {
        var webRoot = FindWebRoot(automation, window);
        var searchButtons = webRoot
            .FindAllDescendants(condition => condition.ByControlType(ControlType.Button))
            .Where(element => string.Equals(NameOf(element), "搜索", StringComparison.Ordinal))
            .Where(element => element.Properties.IsEnabled.ValueOrDefault && !element.Properties.IsOffscreen.ValueOrDefault)
            .ToArray();

        if (searchButtons.Length != 1)
        {
            throw new DesktopVersionUnsupportedException(
                $"会话不在当前侧栏，且没有找到唯一搜索入口：{exactTitle}。可见候选：{DescribeThreadCandidates(webRoot)}");
        }

        searchButtons[0].AsButton().Invoke();
        Thread.Sleep(250);
        webRoot = FindWebRoot(automation, window);
        var searchInputs = webRoot
            .FindAllDescendants(condition => condition.ByControlType(ControlType.Edit))
            .Where(element => element.Properties.IsEnabled.ValueOrDefault && !element.Properties.IsOffscreen.ValueOrDefault)
            .Where(element =>
            {
                var name = NameOf(element);
                var className = element.Properties.ClassName.ValueOrDefault ?? string.Empty;
                return name.Contains("搜索", StringComparison.OrdinalIgnoreCase) ||
                    className.Contains("search", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();

        if (searchInputs.Length != 1)
        {
            throw new DesktopVersionUnsupportedException(
                $"已打开搜索但没有找到唯一搜索输入框：{exactTitle}");
        }

        searchInputs[0].Focus();
        FlaUI.Core.Input.Keyboard.TypeSimultaneously(
            FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL,
            FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_A);
        FlaUI.Core.Input.Keyboard.Type(NormalizeThreadName(exactTitle));
        Thread.Sleep(350);
    }

    internal AutomationElement FindUniqueComposer(
        UIA3Automation automation,
        AutomationElement window)
    {
        ArgumentNullException.ThrowIfNull(automation);
        ArgumentNullException.ThrowIfNull(window);

        var webRoot = FindWebRoot(automation, window);
        var matches = webRoot
            .FindAllDescendants(condition => condition.ByControlType(ControlType.Edit))
            .Where(element =>
            {
                var className = element.Properties.ClassName.ValueOrDefault ?? string.Empty;
                var name = element.Properties.Name.ValueOrDefault ?? string.Empty;
                var looksLikeComposer = className.Contains("ProseMirror", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("输入", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("message", StringComparison.OrdinalIgnoreCase);
                return looksLikeComposer &&
                       element.Properties.IsEnabled.ValueOrDefault &&
                       !element.Properties.IsOffscreen.ValueOrDefault;
            })
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new DesktopVersionUnsupportedException("没有找到唯一的输入框。"),
            _ => throw new DesktopVersionUnsupportedException("找到多个输入框，已停止操作。"),
        };
    }

    private static bool IsThreadButton(AutomationElement element)
    {
        var className = element.Properties.ClassName.ValueOrDefault ?? string.Empty;
        // Codex has changed its CSS class names several times. Keep the
        // semantic safety checks, but do not depend on one presentation class.
        return (element.ControlType == ControlType.Button || element.ControlType == ControlType.ListItem) &&
               !className.Contains("cursor-grab", StringComparison.OrdinalIgnoreCase) &&
               element.Properties.IsEnabled.ValueOrDefault &&
               !element.Properties.IsOffscreen.ValueOrDefault;
    }

    private static AutomationElement FindWebRoot(UIA3Automation automation, AutomationElement window)
    {
        var handle = window.Properties.NativeWindowHandle.ValueOrDefault;
        var nativeChildren = NativeWindowEnumerator.EnumerateDescendants(handle);
        var renderWidgets = nativeChildren
            .Where(child => string.Equals(
                child.ClassName,
                "Chrome_RenderWidgetHostHWND",
                StringComparison.Ordinal))
            .ToArray();

        if (renderWidgets.Length != 1)
        {
            throw new DesktopVersionUnsupportedException(
                $"RenderWidget 数量不是 1：{renderWidgets.Length}");
        }

        return automation.FromHandle(new nint(renderWidgets[0].Handle));
    }

    private static void WaitUntilNavigationCompletes(
        UIA3Automation automation,
        AutomationElement window,
        string exactTitle)
    {
        var deadline = DateTime.UtcNow + NavigationTimeout;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(100);
            var webRoot = FindWebRoot(automation, window);
            var rootRectangle = webRoot.Properties.BoundingRectangle.ValueOrDefault;
            var headerMatches = webRoot
                .FindAllDescendants()
                .Where(element =>
                {
                    var rectangle = element.Properties.BoundingRectangle.ValueOrDefault;
                    return SameThreadName(NameOf(element), exactTitle) &&
                           !element.Properties.IsOffscreen.ValueOrDefault &&
                           rectangle.Left > rootRectangle.Left + (rootRectangle.Width * 0.25) &&
                           rectangle.Top < rootRectangle.Top + 120;
                })
                .ToArray();

            if (headerMatches.Length > 0)
            {
                return;
            }
        }

        throw new DesktopVersionUnsupportedException(
            $"切换后未验证到主内容区标题：{exactTitle}");
    }

    private static string NameOf(AutomationElement element) =>
        element.Properties.Name.ValueOrDefault ?? string.Empty;

    // Codex Desktop has used both "返回标题" and "返回：标题" as the
    // accessible label. Match those semantically, while still requiring one
    // enabled visible navigation item so we never guess a destination.
    private static bool SameThreadName(string candidate, string expected) =>
        string.Equals(NormalizeThreadName(candidate), NormalizeThreadName(expected), StringComparison.Ordinal);

    private static string NormalizeThreadName(string value)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith("返回", StringComparison.Ordinal))
            normalized = normalized[2..].TrimStart('：', ':', ' ', '\t');
        return normalized;
    }

    private static string DescribeThreadCandidates(AutomationElement webRoot)
    {
        var names = webRoot.FindAllDescendants()
            .Where(IsThreadButton)
            .Select(NameOf)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToArray();
        return names.Length == 0 ? "无" : string.Join(" | ", names);
    }
}
