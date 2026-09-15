using Microsoft.Win32;

namespace CodexBridge.App;

public interface IAutostartRegistry
{
    string? Read();
    void Write(string command);
    void Delete();
}

public sealed class AutostartManager
{
    private readonly string _expectedCommand;
    private readonly IAutostartRegistry _registry;

    public AutostartManager(string executablePath, IAutostartRegistry? registry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _expectedCommand = $"\"{Path.GetFullPath(executablePath)}\" --background";
        _registry = registry ?? new CurrentUserRunRegistry();
    }

    public bool IsEnabled => string.Equals(
        _registry.Read(),
        _expectedCommand,
        StringComparison.OrdinalIgnoreCase);

    public void SetEnabled(bool enabled)
    {
        if (enabled) _registry.Write(_expectedCommand);
        else _registry.Delete();
    }

    public string ExpectedCommand => _expectedCommand;
}

internal sealed class CurrentUserRunRegistry : IAutostartRegistry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexBridge";

    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) as string;
    }

    public void Write(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户自启注册表项。");
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
