namespace CodexBridge.Windows;

public sealed record UiElementSnapshot(
    int Index,
    string ControlType,
    string Name,
    string AutomationId,
    string ClassName,
    bool IsEnabled,
    bool IsOffscreen,
    double Left,
    double Top,
    double Width,
    double Height);

public sealed record UiTreeSnapshot(
    WindowIdentity Window,
    IReadOnlyList<UiElementSnapshot> Elements,
    IReadOnlyList<UiElementSnapshot> GlobalMatches,
    IReadOnlyList<NativeWindowInfo> NativeChildren,
    IReadOnlyList<UiElementSnapshot> FragmentElements);
