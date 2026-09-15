using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexBridge.Host.Services;

public interface IDesktopStatusProbe
{
    bool IsOnline();
}

public sealed class DesktopStatusService : IDesktopStatusProbe
{
    public bool IsOnline()
    {
        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            using (process)
            {
                var executablePath = TryGetExecutablePath(process.Id);
                if (executablePath?.Contains(
                        @"\WindowsApps\OpenAI.Codex_",
                        StringComparison.OrdinalIgnoreCase) == true)
                    return true;
            }
        }

        return false;
    }

    private static string? TryGetExecutablePath(int processId)
    {
        const uint processQueryLimitedInformation = 0x1000;
        var handle = OpenProcess(processQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var capacity = 1024u;
            var path = new StringBuilder((int)capacity);
            return QueryFullProcessImageName(handle, 0, path, ref capacity)
                ? path.ToString()
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr process,
        uint flags,
        StringBuilder executablePath,
        ref uint size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
