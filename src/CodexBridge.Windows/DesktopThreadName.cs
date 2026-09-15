namespace CodexBridge.Windows;

public static class DesktopThreadName
{
    public static string FromDatabaseTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title) ||
            title.Length > 512 ||
            !string.Equals(title, title.Trim(), StringComparison.Ordinal) ||
            title.Any(char.IsControl))
        {
            throw new DesktopVersionUnsupportedException("会话标题无法安全映射到 Desktop 控件。");
        }

        // Catalog rows may already include the UIA "返回：" prefix while
        // Codex Desktop exposes the target as "返回标题". Normalize both
        // forms before UI Automation lookup and never duplicate the prefix.
        var normalized = title.StartsWith("返回", StringComparison.Ordinal)
            ? title[2..].TrimStart('：', ':', ' ', '\t')
            : title;
        return $"返回{normalized}";
    }
}
