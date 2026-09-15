using System.Diagnostics;

namespace CodexBridge.Setup;

public sealed class InstalledProcessTerminator(string installDirectory)
{
    public const string ShutdownEventName = @"Local\CodexBridge.InstallShutdown";

    private static readonly string[] ExecutableNames =
    [
        "CodexBridge.App.exe",
        "CodexBridge.Transport.exe",
    ];

    private readonly string _installDirectory =
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory));

    public void Stop()
    {
        var targets = FindTargets();
        if (targets.Count == 0) return;

        if (TrySignalGracefulShutdown())
            WaitForExit(targets, TimeSpan.FromSeconds(10));

        foreach (var process in FindTargets())
        {
            using (process)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    if (!process.WaitForExit(5000))
                        throw new IOException($"无法停止正在运行的 Codex Bridge 进程（PID {process.Id}）。");
                }
                catch (InvalidOperationException)
                {
                    // The process exited between discovery and termination.
                }
            }
        }

        var remaining = FindTargets();
        try
        {
            if (remaining.Count > 0)
                throw new IOException("Codex Bridge 仍在运行，请退出托盘程序后重试。");
        }
        finally
        {
            foreach (var process in remaining) process.Dispose();
        }
    }

    private List<Process> FindTargets()
    {
        var targets = new List<Process>();
        foreach (var executableName in ExecutableNames)
        {
            var expectedPath = Path.Combine(_installDirectory, executableName);
            var processName = Path.GetFileNameWithoutExtension(executableName);
            foreach (var process in Process.GetProcessesByName(processName))
            {
                if (IsExpectedExecutable(process, expectedPath)) targets.Add(process);
                else process.Dispose();
            }
        }
        return targets;
    }

    private static bool IsExpectedExecutable(Process process, string expectedPath)
    {
        try
        {
            var actualPath = process.MainModule?.FileName;
            return actualPath is not null && string.Equals(
                Path.GetFullPath(actualPath),
                Path.GetFullPath(expectedPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool TrySignalGracefulShutdown()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ShutdownEventName, out var shutdownEvent)) return false;
            using (shutdownEvent) return shutdownEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    private static void WaitForExit(IEnumerable<Process> processes, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero) return;
                    process.WaitForExit((int)Math.Min(remaining.TotalMilliseconds, int.MaxValue));
                }
                catch (InvalidOperationException)
                {
                    // Already exited.
                }
            }
        }
    }
}
