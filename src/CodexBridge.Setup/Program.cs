namespace CodexBridge.Setup;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath)
                ?.Equals("unCodexBridge", StringComparison.OrdinalIgnoreCase) == true && args.Length == 0)
            {
                return LaunchDetachedUninstaller();
            }
            if (args.Length == 0) return Usage();
            var command = args[0].ToLowerInvariant();
            var localAppData = Value(args, "--local-app-data");
            var installDirectory = Value(args, "--install-dir");
            var skipRegistrationForTesting = args.Contains(
                "--skip-registration-for-testing", StringComparer.OrdinalIgnoreCase);
            var registration = new InstallLocationRegistration();
            var selectedInstallDirectory = installDirectory ?? (localAppData is null ? registration.Read() : null);
            var paths = localAppData is null
                ? InstallerPaths.CreateForInstall(selectedInstallDirectory)
                : InstallerPaths.CreateForCommandLine(localAppData, selectedInstallDirectory);
            var installer = new PackageInstaller(paths);
            var processTerminator = new InstalledProcessTerminator(paths.InstallDirectory);
            switch (command)
            {
                case "install":
                case "repair":
                    var source = Value(args, "--source") ?? throw new ArgumentException("缺少 --source。");
                    processTerminator.Stop();
                    installer.InstallOrRepair(source);
                    installer.VerifyInstalled();
                    if (localAppData is null && !skipRegistrationForTesting)
                        registration.Write(paths.InstallDirectory);
                    Console.WriteLine(command == "install" ? "安装完成。" : "修复完成。");
                    return 0;
                case "verify":
                    installer.VerifyInstalled();
                    Console.WriteLine("安装校验通过。");
                    return 0;
                case "uninstall":
                    if (localAppData is null && !skipRegistrationForTesting)
                    {
                        AutostartRegistration.RemoveIfExact(Path.Combine(paths.InstallDirectory, "CodexBridge.App.exe"));
                        registration.RemoveIfExact(paths.InstallDirectory);
                    }
                    processTerminator.Stop();
                    installer.Uninstall(args.Contains("--purge-data", StringComparer.OrdinalIgnoreCase));
                    Console.WriteLine("卸载完成。默认保留设备和项目配置。");
                    return 0;
                default:
                    return Usage();
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"安装器失败：{exception.Message}");
            return 1;
        }
    }

    private static int LaunchDetachedUninstaller()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "CodexBridge-uninstall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var detached = Path.Combine(tempDirectory, "CodexBridge.Setup.exe");
        File.Copy(Environment.ProcessPath!, detached, overwrite: true);
        var installDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = detached,
            UseShellExecute = false,
            Arguments = $"uninstall --install-dir \"{installDirectory}\"",
            WorkingDirectory = tempDirectory,
        });
        return process is null ? 1 : 0;
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.FindIndex(args, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("用法：CodexBridge.Setup install|repair --source <payload> [--install-dir <path>] [--local-app-data <测试路径>] | verify [--install-dir <path>] | uninstall [--install-dir <path>] [--purge-data]");
        return 2;
    }
}
