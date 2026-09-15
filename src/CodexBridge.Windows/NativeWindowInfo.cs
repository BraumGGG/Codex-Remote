namespace CodexBridge.Windows;

public sealed record NativeWindowInfo(
    long Handle,
    int ProcessId,
    string ClassName,
    string Title,
    bool IsVisible);
