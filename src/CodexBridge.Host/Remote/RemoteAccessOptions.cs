namespace CodexBridge.Host.Remote;

public sealed record RemoteAccessOptions(
    bool Enabled,
    Uri? SignalUri,
    Uri? PublicAppUri,
    string IdentityPath,
    string DevicePath,
    string ReceiptPath,
    string TransportExecutablePath,
    string TransportManifestPath,
    bool RequireAuthenticode,
    string EntitlementTokenPath,
    IReadOnlyDictionary<string, string> EntitlementPublicKeys,
    Uri? EntitlementServiceUri,
    string EntitlementCredentialPath)
{
    public static RemoteAccessOptions FromEnvironment()
    {
        var configuredDataDirectory = Environment.GetEnvironmentVariable("CODEX_BRIDGE_DATA_DIRECTORY");
        var data = string.IsNullOrWhiteSpace(configuredDataDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexBridge")
            : Path.GetFullPath(configuredDataDirectory);
        var enabled = Environment.GetEnvironmentVariable("CODEX_BRIDGE_REMOTE_ENABLED") == "1";
        Uri? ReadUri(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
        }
        var baseDirectory = AppContext.BaseDirectory;
        IReadOnlyDictionary<string, string> entitlementKeys;
        try
        {
            entitlementKeys = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                Environment.GetEnvironmentVariable("CODEX_BRIDGE_ENTITLEMENT_PUBLIC_KEYS") ?? "{}")
                ?? new Dictionary<string, string>();
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new InvalidOperationException("Entitlement public keys are invalid.", exception);
        }
        var options = new RemoteAccessOptions(
            enabled,
            ReadUri("CODEX_BRIDGE_SIGNAL_URL"),
            ReadUri("CODEX_BRIDGE_REMOTE_APP_URL"),
            Path.Combine(data, "remote-identity.json"),
            Path.Combine(data, "remote-devices.json"),
            Path.Combine(data, "remote-receipts.json"),
            Environment.GetEnvironmentVariable("CODEX_BRIDGE_TRANSPORT_PATH") ?? Path.Combine(baseDirectory, "CodexBridge.Transport.exe"),
            Environment.GetEnvironmentVariable("CODEX_BRIDGE_TRANSPORT_MANIFEST") ?? Path.Combine(baseDirectory, "transport-manifest.json"),
            ShouldRequireAuthenticode(
                Environment.GetEnvironmentVariable("CODEX_BRIDGE_REQUIRE_AUTHENTICODE")),
            Path.Combine(data, "entitlements.json"),
            entitlementKeys,
            ReadUri("CODEX_BRIDGE_ENTITLEMENT_URL"),
            Path.Combine(data, "entitlement-credentials.json"));
        if (enabled && (options.SignalUri?.Scheme is not ("wss" or "ws") || options.PublicAppUri?.Scheme is not ("https" or "http")))
            throw new InvalidOperationException("Remote mode requires valid Signal and public app URLs.");
        if (enabled && options.SignalUri!.Scheme == "ws" && !options.SignalUri.IsLoopback)
            throw new InvalidOperationException("Insecure Signal URL is only allowed on loopback.");
        if (options.EntitlementServiceUri is not null &&
            options.EntitlementServiceUri.Scheme != Uri.UriSchemeHttps &&
            !(options.EntitlementServiceUri.Scheme == Uri.UriSchemeHttp && options.EntitlementServiceUri.IsLoopback))
            throw new InvalidOperationException("Entitlement service must use HTTPS outside loopback.");
        return options;
    }

    internal static bool ShouldRequireAuthenticode(string? configured) =>
        string.Equals(configured, "1", StringComparison.Ordinal);
}

public sealed record RemotePairingInfo(string Url, DateTimeOffset ExpiresAt);
