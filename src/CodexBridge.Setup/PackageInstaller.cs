using System.Security.Cryptography;

namespace CodexBridge.Setup;

public sealed class PackageInstaller(InstallerPaths paths)
{
    public void InstallOrRepair(string sourceDirectory, bool failAfterBackupForTesting = false)
    {
        paths.AssertSafeTargets();
        var source = Path.GetFullPath(sourceDirectory);
        var manifestPath = Path.Combine(source, PackageManifest.FileName);
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("安装包缺少 package-manifest.json。", manifestPath);
        AssertNoReparsePoints(source);
        var manifest = PackageManifest.Load(manifestPath);
        Directory.CreateDirectory(paths.InstallParentDirectory);
        AssertNotReparsePoint(paths.InstallParentDirectory);
        if (Directory.Exists(paths.InstallDirectory))
        {
            AssertNotReparsePoint(paths.InstallDirectory);
            if (Directory.EnumerateFileSystemEntries(paths.InstallDirectory).Any()) VerifyInstalled();
            else Directory.Delete(paths.InstallDirectory, recursive: false);
        }
        var operationId = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(paths.InstallParentDirectory, $".CodexBridge.staging-{operationId}");
        var backup = Path.Combine(paths.InstallParentDirectory, $".CodexBridge.backup-{operationId}");
        var movedOld = false;
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var file in manifest.Files)
            {
                var sourceFile = ResolveManifestPath(source, file.Path);
                VerifyFile(sourceFile, file);
                var destination = ResolveManifestPath(staging, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(sourceFile, destination, overwrite: false);
                File.SetAttributes(destination, FileAttributes.Normal);
                VerifyFile(destination, file);
            }
            File.Copy(manifestPath, Path.Combine(staging, PackageManifest.FileName), overwrite: false);
            File.SetAttributes(Path.Combine(staging, PackageManifest.FileName), FileAttributes.Normal);

            if (Directory.Exists(paths.InstallDirectory))
            {
                Directory.Move(paths.InstallDirectory, backup);
                movedOld = true;
            }
            if (failAfterBackupForTesting) throw new IOException("模拟升级切换失败。");
            Directory.Move(staging, paths.InstallDirectory);
            if (movedOld) DeleteOperationDirectory(backup, ".CodexBridge.backup-");
        }
        catch
        {
            if (Directory.Exists(staging)) DeleteOperationDirectory(staging, ".CodexBridge.staging-");
            if (movedOld && Directory.Exists(backup) && !Directory.Exists(paths.InstallDirectory))
                Directory.Move(backup, paths.InstallDirectory);
            throw;
        }
    }

    public void Uninstall(bool purgeData)
    {
        paths.AssertSafeTargets();
        if (Directory.Exists(paths.InstallDirectory))
        {
            AssertNotReparsePoint(paths.InstallDirectory);
            DeleteDirectoryTree(paths.InstallDirectory);
        }
        if (purgeData && Directory.Exists(paths.DataDirectory))
        {
            AssertNotReparsePoint(paths.DataDirectory);
            DeleteDirectoryTree(paths.DataDirectory);
        }
    }

    public void VerifyInstalled()
    {
        paths.AssertSafeTargets();
        var manifestPath = Path.Combine(paths.InstallDirectory, PackageManifest.FileName);
        var manifest = PackageManifest.Load(manifestPath);
        foreach (var file in manifest.Files)
            VerifyFile(ResolveManifestPath(paths.InstallDirectory, file.Path), file);
    }

    private static string ResolveManifestPath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException("安装包包含非法路径。");
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, normalized));
        if (!candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("安装包路径越界。");
        return candidate;
    }

    private static void VerifyFile(string path, PackageFile expected)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != expected.Length)
            throw new InvalidDataException($"安装包文件长度不匹配：{expected.Path}");
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(hash, expected.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"安装包文件哈希不匹配：{expected.Path}");
    }

    private static void AssertNoReparsePoints(string root)
    {
        AssertNotReparsePoint(root);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            foreach (var directory in Directory.EnumerateDirectories(pending.Pop(), "*", SearchOption.TopDirectoryOnly))
            {
                AssertNotReparsePoint(directory);
                pending.Push(directory);
            }
        }
    }

    private static void AssertNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("安装器拒绝重解析点目录。");
    }

    private void DeleteOperationDirectory(string path, string requiredPrefix)
    {
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(paths.InstallParentDirectory), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith(requiredPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("拒绝清理非安装器临时目录。");
        }
        DeleteDirectoryTree(full);
    }

    private static void DeleteDirectoryTree(string root)
    {
        var pending = new Stack<(string Path, bool Visited)>();
        pending.Push((root, false));
        while (pending.Count > 0)
        {
            var (current, visited) = pending.Pop();
            if (visited)
            {
                File.SetAttributes(current, FileAttributes.Normal);
                Directory.Delete(current, recursive: false);
                continue;
            }

            AssertNotReparsePoint(current);
            pending.Push((current, true));
            foreach (var file in Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("安装器拒绝重解析点文件。");
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            foreach (var directory in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
                pending.Push((directory, false));
        }
    }
}
