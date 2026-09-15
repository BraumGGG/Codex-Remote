using Microsoft.Win32;

namespace CodexBridge.Setup;

public static class AutostartRegistration
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexBridge";

    public static bool IsExactCommand(string? command, string executablePath) =>
        string.Equals(
            command,
            $"\"{Path.GetFullPath(executablePath)}\" --background",
            StringComparison.OrdinalIgnoreCase);

    public static void RemoveIfExact(string executablePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
        if (key is null) return;
        if (IsExactCommand(key.GetValue(ValueName) as string, executablePath))
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
