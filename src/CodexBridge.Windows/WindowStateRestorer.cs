using System.Runtime.InteropServices;

namespace CodexBridge.Windows;

internal sealed class WindowStateRestorer : IDisposable
{
    private const int SwMinimize = 6;
    private const int SwRestore = 9;
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(2);

    private readonly nint _handle;
    private readonly bool _wasMinimized;
    private bool _disposed;

    private WindowStateRestorer(nint handle, bool wasMinimized)
    {
        _handle = handle;
        _wasMinimized = wasMinimized;
    }

    public bool WasMinimized => _wasMinimized;

    public static WindowStateRestorer Activate(nint handle)
    {
        if (handle == nint.Zero)
        {
            throw new ArgumentException("窗口句柄无效。", nameof(handle));
        }

        var wasMinimized = IsIconic(handle);
        if (wasMinimized)
        {
            ShowWindow(handle, SwRestore);
            var deadline = DateTime.UtcNow + ActivationTimeout;
            while (IsIconic(handle) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }

        if (!TryActivate(handle))
        {
            if (wasMinimized)
            {
                ShowWindow(handle, SwMinimize);
            }

            throw new InteractiveSessionUnavailableException("无法将 Codex Desktop 激活到前台。");
        }

        return new WindowStateRestorer(handle, wasMinimized);
    }

    private static bool TryActivate(nint handle)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ShowWindow(handle, SwRestore);
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
            if (WaitForForeground(handle, TimeSpan.FromMilliseconds(250)))
            {
                return true;
            }
        }

        var currentThread = GetCurrentThreadId();
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), nint.Zero);
        var targetThread = GetWindowThreadProcessId(handle, nint.Zero);
        var attachedForeground = foregroundThread != 0 &&
                                 foregroundThread != currentThread &&
                                 AttachThreadInput(currentThread, foregroundThread, true);
        var attachedTarget = targetThread != 0 &&
                             targetThread != currentThread &&
                             targetThread != foregroundThread &&
                             AttachThreadInput(currentThread, targetThread, true);
        try
        {
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
            return WaitForForeground(handle, TimeSpan.FromMilliseconds(500));
        }
        finally
        {
            if (attachedTarget)
            {
                AttachThreadInput(currentThread, targetThread, false);
            }

            if (attachedForeground)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    private static bool WaitForForeground(nint handle, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            if (GetForegroundWindow() == handle)
            {
                return true;
            }

            Thread.Sleep(25);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_wasMinimized)
        {
            ShowWindow(_handle, SwMinimize);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, nint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
}
