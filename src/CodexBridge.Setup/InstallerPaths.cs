namespace CodexBridge.Setup;

public sealed record InstallerPaths(string LocalAppData, string InstallDirectory)
{
    public string InstallParentDirectory =>
        Path.GetDirectoryName(InstallDirectory) ?? throw new InvalidOperationException("安装目录不能是磁盘根目录。");

    public string DataDirectory => Path.Combine(LocalAppData, "CodexBridge");

    public static InstallerPaths Create(string? localAppDataOverride = null)
    {
        var localAppData = ResolveLocalAppData(localAppDataOverride);
        return new InstallerPaths(localAppData, Path.Combine(localAppData, "Programs", "CodexBridge"));
    }

    public static InstallerPaths CreateForInstall(string? installDirectory, string? localAppDataOverride = null)
    {
        var localAppData = ResolveLocalAppData(localAppDataOverride);
        if (string.IsNullOrWhiteSpace(installDirectory)) return Create(localAppData);
        return new InstallerPaths(localAppData, ValidateInstallDirectory(installDirectory));
    }

    public static InstallerPaths CreateForCommandLine(string? testRoot, string? installDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(testRoot)) return Create();
        var allowedRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".test-state"));
        var candidate = Path.GetFullPath(testRoot);
        if (!IsDescendant(candidate, allowedRoot))
        {
            throw new InvalidOperationException(
                "测试 LocalAppData 必须位于当前目录的 .test-state 内。再发行安装不能覆盖 LocalAppData。 ");
        }
        if (string.IsNullOrWhiteSpace(installDirectory)) return Create(candidate);
        var install = Path.GetFullPath(installDirectory);
        if (!IsDescendant(install, allowedRoot))
            throw new InvalidOperationException("测试安装目录必须位于当前目录的 .test-state 内。");
        return CreateForInstall(install, candidate);
    }

    public void AssertSafeTargets()
    {
        var normalized = ValidateInstallDirectory(InstallDirectory);
        if (!string.Equals(normalized, Path.TrimEndingDirectorySeparator(Path.GetFullPath(InstallDirectory)),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("安装目录规范化结果不一致。");
        AssertDirectChild(DataDirectory, LocalAppData, "CodexBridge");
    }

    private static string ResolveLocalAppData(string? localAppDataOverride)
    {
        var root = string.IsNullOrWhiteSpace(localAppDataOverride)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localAppDataOverride;
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("无法确定 LocalAppData 路径。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    private static string ValidateInstallDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidOperationException("安装目录必须是本机磁盘上的绝对路径。");
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(candidate);
        if (string.IsNullOrWhiteSpace(root) || string.Equals(candidate, Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("安装目录不能是磁盘根目录。");
        if (new DriveInfo(root).DriveType != DriveType.Fixed)
            throw new InvalidOperationException("安装目录必须位于本机固定磁盘。");

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windows) &&
            (string.Equals(candidate, Path.TrimEndingDirectorySeparator(windows), StringComparison.OrdinalIgnoreCase) ||
             IsDescendant(candidate, windows)))
            throw new InvalidOperationException("安装目录不能位于 Windows 系统目录。");

        AssertExistingPathChainHasNoReparsePoint(candidate);
        return candidate;
    }

    private static void AssertExistingPathChainHasNoReparsePoint(string candidate)
    {
        var existing = candidate;
        while (!Directory.Exists(existing))
        {
            existing = Path.GetDirectoryName(existing)
                ?? throw new InvalidOperationException("无法确定安装目录的现有父目录。");
        }
        var root = Path.GetPathRoot(existing)!;
        var current = root;
        foreach (var segment in existing[root.Length..].Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("安装器拒绝重解析点目录。");
        }
    }

    private static bool IsDescendant(string candidate, string parent)
    {
        var fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var fullParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return fullCandidate.StartsWith(fullParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertDirectChild(string candidate, string parent, string expectedName)
    {
        var fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var fullParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        if (!string.Equals(Path.GetFileName(fullCandidate), expectedName, StringComparison.Ordinal) ||
            !string.Equals(Path.GetDirectoryName(fullCandidate), fullParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("安装器数据目录不符合安全约束。");
    }
}
