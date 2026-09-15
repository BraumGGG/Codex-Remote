namespace CodexBridge.Windows;

public static class UiSelectionPolicy
{
    public static UiElementSnapshot SelectThreadButton(
        IEnumerable<UiElementSnapshot> elements,
        string exactTitle)
    {
        ArgumentNullException.ThrowIfNull(elements);
        ArgumentException.ThrowIfNullOrWhiteSpace(exactTitle);

        var matches = elements
            .Where(element =>
                element.ControlType == "Button" &&
                string.Equals(element.Name, exactTitle, StringComparison.Ordinal) &&
                element.ClassName.Contains("sidebar-item", StringComparison.Ordinal) &&
                !element.ClassName.Contains("cursor-grab", StringComparison.Ordinal) &&
                element.IsEnabled &&
                !element.IsOffscreen)
            .ToArray();

        return RequireSingle(matches, "会话按钮");
    }

    public static UiElementSnapshot SelectComposer(IEnumerable<UiElementSnapshot> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);

        var matches = elements
            .Where(element =>
                element.ControlType == "Edit" &&
                string.Equals(element.Name, "随心输入", StringComparison.Ordinal) &&
                element.ClassName.Contains("ProseMirror", StringComparison.Ordinal) &&
                element.IsEnabled &&
                !element.IsOffscreen)
            .ToArray();

        return RequireSingle(matches, "输入框");
    }

    private static T RequireSingle<T>(IReadOnlyList<T> matches, string label)
    {
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"没有找到唯一的{label}。"),
            _ => throw new InvalidOperationException($"找到多个{label}，已停止操作。"),
        };
    }
}
