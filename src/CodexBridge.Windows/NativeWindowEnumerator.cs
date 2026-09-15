using System.Runtime.InteropServices;
using System.Text;

namespace CodexBridge.Windows;

internal static class NativeWindowEnumerator
{
    private delegate bool EnumChildProc(nint windowHandle, nint parameter);

    public static IReadOnlyList<NativeWindowInfo> EnumerateDescendants(nint parentWindow)
    {
        var windows = new List<NativeWindowInfo>();
        EnumChildProc callback = (handle, parameter) =>
        {
            _ = GetWindowThreadProcessId(handle, out var processId);
            windows.Add(new NativeWindowInfo(
                handle.ToInt64(),
                checked((int)processId),
                ReadClassName(handle),
                ReadWindowText(handle),
                IsWindowVisible(handle)));
            return true;
        };

        if (!EnumChildWindows(parentWindow, callback, 0))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        GC.KeepAlive(callback);
        return windows;
    }

    private static string ReadClassName(nint handle)
    {
        var buffer = new StringBuilder(512);
        return GetClassName(handle, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    private static string ReadWindowText(nint handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        return GetWindowText(handle, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint parentWindow, EnumChildProc callback, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(nint windowHandle, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(nint windowHandle, StringBuilder text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);
}
