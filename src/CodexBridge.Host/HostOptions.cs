namespace CodexBridge.Host;

public sealed record HostOptions(
    string ListenUrl,
    string StateDatabasePath,
    string AuditLogPath,
    string ConfigurationPath)
{
    public static HostOptions CreateDefault()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configuredDataDirectory = Environment.GetEnvironmentVariable("CODEX_BRIDGE_DATA_DIRECTORY");
        var dataDirectory = string.IsNullOrWhiteSpace(configuredDataDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexBridge")
            : Path.GetFullPath(configuredDataDirectory);
        var configuredStateDatabase = Environment.GetEnvironmentVariable("CODEX_BRIDGE_STATE_DATABASE");

        return new HostOptions(
            "http://127.0.0.1:5096",
            string.IsNullOrWhiteSpace(configuredStateDatabase)
                ? Path.Combine(userProfile, ".codex", "state_5.sqlite")
                : Path.GetFullPath(configuredStateDatabase),
            Path.Combine(dataDirectory, "audit.jsonl"),
            Path.Combine(dataDirectory, "bridge-config.json"));
    }

    public void EnsureLoopbackOnly()
    {
        if (!Uri.TryCreate(ListenUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp ||
            !System.Net.IPAddress.TryParse(uri.Host, out var address) ||
            !System.Net.IPAddress.IsLoopback(address))
        {
            throw new InvalidOperationException("Host 只允许监听 loopback HTTP 地址。");
        }
    }
}
