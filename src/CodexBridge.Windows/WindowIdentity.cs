namespace CodexBridge.Windows;

public sealed record WindowIdentity(
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    string Name,
    long NativeWindowHandle);
