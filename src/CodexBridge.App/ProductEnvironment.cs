namespace CodexBridge.App;

public static class ProductEnvironment
{
    private static readonly IReadOnlyDictionary<string, string> Defaults =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CODEX_BRIDGE_REMOTE_ENABLED"] = "1",
            ["CODEX_BRIDGE_SIGNAL_URL"] = "wss://remote.example.invalid:8443/signal",
            ["CODEX_BRIDGE_REMOTE_APP_URL"] = "https://remote.example.invalid:8443/remote/",
            ["CODEX_BRIDGE_ENTITLEMENT_URL"] = "https://remote.example.invalid:8443/",
            ["CODEX_BRIDGE_ENTITLEMENT_PUBLIC_KEYS"] =
                "{\"key-20260817-85b8a4e8\":\"MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEiQThbKFGootOaFXeoqc7HmsKhFjAJ85Y5INnd2atpDm0lWzZIuMVKs2ySKNOkUntW4qC6v1irhxJ9Rzq40qdDg\"}",
        };

    public static void Apply()
    {
        foreach (var (name, value) in Defaults)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    public static IReadOnlyDictionary<string, string> ProductDefaults => Defaults;
}
