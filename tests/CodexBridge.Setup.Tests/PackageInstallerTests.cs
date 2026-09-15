using System.Security.Cryptography;
using System.Text.Json;

namespace CodexBridge.Setup.Tests;

public sealed class PackageInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
        ".test-state", "setup", Guid.NewGuid().ToString("N"));

    [Fact]
    public void InstallUpgradeRollbackUninstallAndReinstall_PreserveDataByDefault()
    {
        var local = Path.Combine(_root, "LocalAppData");
        var paths = InstallerPaths.Create(local);
        var installer = new PackageInstaller(paths);
        var v1 = CreatePayload("1.0.0", "version-one");
        var v2 = CreatePayload("1.1.0", "version-two");
        File.SetAttributes(Path.Combine(v1, "app.txt"), FileAttributes.ReadOnly);
        Directory.CreateDirectory(paths.DataDirectory);
        var device = Path.Combine(paths.DataDirectory, "devices.json");
        var config = Path.Combine(paths.DataDirectory, "bridge-config.json");
        File.WriteAllText(device, "device-identity");
        File.WriteAllText(config, "project-config");
        var deviceHash = Hash(device);
        var configHash = Hash(config);

        installer.InstallOrRepair(v1);
        installer.VerifyInstalled();
        Assert.Equal("version-one", File.ReadAllText(Path.Combine(paths.InstallDirectory, "app.txt")));

        installer.InstallOrRepair(v2);
        Assert.Equal("version-two", File.ReadAllText(Path.Combine(paths.InstallDirectory, "app.txt")));
        Assert.Equal(deviceHash, Hash(device));
        Assert.Equal(configHash, Hash(config));

        Assert.Throws<IOException>(() => installer.InstallOrRepair(v1, failAfterBackupForTesting: true));
        Assert.Equal("version-two", File.ReadAllText(Path.Combine(paths.InstallDirectory, "app.txt")));
        Assert.Empty(Directory.GetDirectories(paths.InstallParentDirectory, ".CodexBridge.*-*"));

        installer.Uninstall(purgeData: false);
        Assert.False(Directory.Exists(paths.InstallDirectory));
        Assert.Equal(deviceHash, Hash(device));
        Assert.Equal(configHash, Hash(config));

        installer.InstallOrRepair(v1);
        installer.Uninstall(purgeData: true);
        Assert.False(Directory.Exists(paths.InstallDirectory));
        Assert.False(Directory.Exists(paths.DataDirectory));
    }

    [Fact]
    public void Install_RejectsTraversalTamperingAndDuplicatePaths()
    {
        var paths = InstallerPaths.Create(Path.Combine(_root, "LocalAppData-security"));
        var installer = new PackageInstaller(paths);
        var payload = CreatePayload("1.0.0", "safe");
        var manifestPath = Path.Combine(payload, PackageManifest.FileName);
        var manifest = PackageManifest.Load(manifestPath);

        WriteManifest(manifestPath, manifest with
        {
            Files = [manifest.Files[0] with { Path = "../escape.txt" }],
        });
        Assert.Throws<InvalidDataException>(() => installer.InstallOrRepair(payload));

        payload = CreatePayload("1.0.1", "safe");
        manifestPath = Path.Combine(payload, PackageManifest.FileName);
        manifest = PackageManifest.Load(manifestPath);
        File.WriteAllText(Path.Combine(payload, "app.txt"), "tampered");
        Assert.Throws<InvalidDataException>(() => installer.InstallOrRepair(payload));

        payload = CreatePayload("1.0.2", "safe");
        manifestPath = Path.Combine(payload, PackageManifest.FileName);
        manifest = PackageManifest.Load(manifestPath);
        WriteManifest(manifestPath, manifest with { Files = [manifest.Files[0], manifest.Files[0]] });
        Assert.Throws<InvalidDataException>(() => installer.InstallOrRepair(payload));

        payload = CreatePayload("1.0.3", "safe");
        manifestPath = Path.Combine(payload, PackageManifest.FileName);
        manifest = PackageManifest.Load(manifestPath);
        WriteManifest(manifestPath, manifest with { Files = [manifest.Files[0] with { Path = "app.txt:stream" }] });
        Assert.Throws<InvalidDataException>(() => installer.InstallOrRepair(payload));

        payload = CreatePayload("1.0.4", "safe");
        manifestPath = Path.Combine(payload, PackageManifest.FileName);
        manifest = PackageManifest.Load(manifestPath);
        WriteManifest(manifestPath, manifest with
        {
            Files = [manifest.Files[0] with { Path = "folder/app.txt" }, manifest.Files[0] with { Path = "folder\\app.txt" }],
        });
        Assert.Throws<InvalidDataException>(() => installer.InstallOrRepair(payload));
        Assert.False(Directory.Exists(paths.InstallDirectory));
    }

    [Fact]
    public void CommandLineTestRootAndAutostart_AreStrictlyScoped()
    {
        var current = Environment.CurrentDirectory;
        var allowed = Path.Combine(current, ".test-state", "setup-cli", Guid.NewGuid().ToString("N"));
        Assert.Equal(Path.GetFullPath(allowed), InstallerPaths.CreateForCommandLine(allowed).LocalAppData);
        Assert.Throws<InvalidOperationException>(() => InstallerPaths.CreateForCommandLine(Path.Combine(current, "outside")));

        var executable = Path.Combine(_root, "Programs", "CodexBridge", "CodexBridge.App.exe");
        Assert.True(AutostartRegistration.IsExactCommand($"\"{Path.GetFullPath(executable)}\" --background", executable));
        Assert.False(AutostartRegistration.IsExactCommand($"\"{Path.GetFullPath(executable)}\" --background --extra", executable));
        Assert.False(AutostartRegistration.IsExactCommand("malicious.exe --background", executable));
    }

    [Fact]
    public void CustomInstallDirectory_RemainsSeparateFromDataAndRejectsForeignContent()
    {
        var local = Path.Combine(_root, "LocalAppData-custom");
        var install = Path.Combine(_root, "自定义 安装目录", "CodexBridge");
        var paths = InstallerPaths.CreateForInstall(install, local);
        var installer = new PackageInstaller(paths);
        var payload = CreatePayload("2.0.0", "custom-install");

        installer.InstallOrRepair(payload);
        installer.VerifyInstalled();
        Assert.Equal(Path.GetFullPath(install), paths.InstallDirectory);
        Assert.Equal(Path.Combine(Path.GetFullPath(local), "CodexBridge"), paths.DataDirectory);

        installer.Uninstall(purgeData: false);
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "foreign.txt"), "not-codex-bridge");
        Assert.ThrowsAny<Exception>(() => installer.InstallOrRepair(payload));
        Assert.Equal("not-codex-bridge", File.ReadAllText(Path.Combine(install, "foreign.txt")));
    }

    [Fact]
    public void InstallPaths_RejectRelativeUncRootAndTestEscape()
    {
        Assert.Throws<InvalidOperationException>(() => InstallerPaths.CreateForInstall("relative\\CodexBridge"));
        Assert.Throws<InvalidOperationException>(() => InstallerPaths.CreateForInstall(@"\\server\share\CodexBridge"));
        Assert.Throws<InvalidOperationException>(() => InstallerPaths.CreateForInstall(Path.GetPathRoot(_root)!));

        var testRoot = Path.Combine(Environment.CurrentDirectory, ".test-state", "setup-paths", Guid.NewGuid().ToString("N"));
        var inside = Path.Combine(testRoot, "Custom", "CodexBridge");
        Assert.Equal(Path.GetFullPath(inside), InstallerPaths.CreateForCommandLine(testRoot, inside).InstallDirectory);
        Assert.Throws<InvalidOperationException>(() =>
            InstallerPaths.CreateForCommandLine(testRoot, Path.Combine(Environment.CurrentDirectory, "outside")));
    }

    [Fact]
    public void InstallLocationRegistration_NormalizesAndRemovesOnlyExactValue()
    {
        var store = new MemoryInstallLocationStore();
        var registration = new InstallLocationRegistration(store);
        var expected = Path.Combine(_root, "registered", "CodexBridge");

        registration.Write(expected + Path.DirectorySeparatorChar);
        Assert.Equal(Path.GetFullPath(expected), registration.Read());
        registration.RemoveIfExact(Path.Combine(_root, "other", "CodexBridge"));
        Assert.False(store.Removed);
        registration.RemoveIfExact(expected);
        Assert.True(store.Removed);
    }

    [Fact]
    public void InstalledProcessTerminator_StopsOnlyProcessFromTargetDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;
        var install = Path.Combine(_root, "occupied", "CodexBridge");
        var foreign = Path.Combine(_root, "foreign");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(foreign);
        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var targetExecutable = Path.Combine(install, "CodexBridge.App.exe");
        var foreignExecutable = Path.Combine(foreign, "CodexBridge.App.exe");
        File.Copy(source, targetExecutable);
        File.Copy(source, foreignExecutable);

        using var target = StartLongRunningCommand(targetExecutable);
        using var unrelated = StartLongRunningCommand(foreignExecutable);
        try
        {
            new InstalledProcessTerminator(install).Stop();
            Assert.True(target.WaitForExit(5000));
            Assert.False(unrelated.HasExited);
        }
        finally
        {
            if (!target.HasExited) target.Kill(entireProcessTree: true);
            if (!unrelated.HasExited) unrelated.Kill(entireProcessTree: true);
            target.WaitForExit(5000);
            unrelated.WaitForExit(5000);
        }
    }

    private string CreatePayload(string version, string content)
    {
        var directory = Path.Combine(_root, $"payload-{version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "app.txt");
        File.WriteAllText(file, content);
        WriteManifest(Path.Combine(directory, PackageManifest.FileName), new PackageManifest(
            PackageManifest.CurrentSchemaVersion,
            version,
            [new PackageFile("app.txt", new FileInfo(file).Length, Hash(file))]));
        return directory;
    }

    private static void WriteManifest(string path, PackageManifest manifest) =>
        File.WriteAllText(path, JsonSerializer.Serialize(manifest));

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static System.Diagnostics.Process StartLongRunningCommand(string executable) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = executable,
            Arguments = "/d /c ping.exe 127.0.0.1 -t",
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("无法启动安装占用测试进程。");

    private sealed class MemoryInstallLocationStore : IInstallLocationStore
    {
        public string? Value { get; private set; }
        public bool Removed { get; private set; }
        public string? Read() => Value;
        public void Write(string installDirectory) => Value = installDirectory;
        public void Remove()
        {
            Removed = true;
            Value = null;
        }
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }
}
