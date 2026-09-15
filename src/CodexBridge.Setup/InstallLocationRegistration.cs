using Microsoft.Win32;

namespace CodexBridge.Setup;

public interface IInstallLocationStore
{
    string? Read();
    void Write(string installDirectory);
    void Remove();
}

public sealed class InstallLocationRegistration(IInstallLocationStore store)
{
    public InstallLocationRegistration() : this(new CurrentUserInstallLocationStore())
    {
    }

    public string? Read()
    {
        var value = store.Read();
        return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);
    }

    public void Write(string installDirectory) =>
        store.Write(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory)));

    public void RemoveIfExact(string installDirectory)
    {
        var registered = Read();
        var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory));
        if (string.Equals(registered, expected, StringComparison.OrdinalIgnoreCase)) store.Remove();
    }

    private sealed class CurrentUserInstallLocationStore : IInstallLocationStore
    {
        private const string RegistryPath = @"Software\CodexBridge";
        private const string ValueName = "InstallLocation";

        public string? Read()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
            return key?.GetValue(ValueName) as string;
        }

        public void Write(string installDirectory)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
            key.SetValue(ValueName, installDirectory, RegistryValueKind.String);
        }

        public void Remove()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
