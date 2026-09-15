using System.Globalization;
using System.Text;

namespace CodexBridge.Signal;

public sealed class SignalMetrics(TimeProvider timeProvider)
{
    private readonly DateTimeOffset _startedAt = timeProvider.GetUtcNow();
    private long _authenticatedHosts;
    private long _authenticatedClients;
    private long _authRejected;
    private long _turnCredentials;
    private long _forwarded;
    private long _forwardMisses;
    private long _telemetryConnected;
    private long _telemetryFailed;
    private readonly long[] _candidateTypes = new long[4];
    private static readonly string[] ClientStages =
    [
        "signal_authenticated", "answer_verified", "remote_description_set", "ice_connected",
        "peer_connected", "datachannel_open", "first_rpc_sent", "first_rpc_received",
        "datachannel_error", "peer_failed", "channel_closed",
        "offer_relay_only", "offer_contains_host", "offer_contains_srflx", "offer_contains_unknown",
        "turn_request_sent", "turn_credentials_received", "offer_created", "ice_gathering_completed",
        "pairing_offer_sent", "offer_sent", "error_turn_credentials_timeout", "error_ice_gathering_timeout",
        "ice_candidate_retry", "error_ice_no_relay_candidate", "error_native_identity_failed", "error_offer_send_failed",
    ];
    private readonly long[] _clientStages = new long[ClientStages.Length];
    private readonly object _versionGate = new();
    private readonly Dictionary<string, long> _versions = new(StringComparer.Ordinal);

    public void AuthenticationSucceeded(string role)
    {
        if (role == "host") Interlocked.Increment(ref _authenticatedHosts);
        else Interlocked.Increment(ref _authenticatedClients);
    }
    public void AuthenticationRejected() => Interlocked.Increment(ref _authRejected);
    public void TurnCredentialIssued() => Interlocked.Increment(ref _turnCredentials);
    public void Forwarded(bool delivered)
    {
        if (delivered) Interlocked.Increment(ref _forwarded);
        else Interlocked.Increment(ref _forwardMisses);
    }

    public void ObserveTelemetry(string outcome, string candidateType, string? version)
    {
        if (outcome == "connected") Interlocked.Increment(ref _telemetryConnected);
        else if (outcome == "failed") Interlocked.Increment(ref _telemetryFailed);
        var candidateIndex = candidateType switch { "host" => 0, "srflx" => 1, "relay" => 2, _ => 3 };
        Interlocked.Increment(ref _candidateTypes[candidateIndex]);
        var normalizedVersion = NormalizeVersion(version);
        lock (_versionGate)
        {
            if (_versions.ContainsKey(normalizedVersion) || _versions.Count < 15)
                _versions[normalizedVersion] = _versions.GetValueOrDefault(normalizedVersion) + 1;
            else
                _versions["other"] = _versions.GetValueOrDefault("other") + 1;
        }
    }

    public void ObserveClientStage(string? stage)
    {
        var index = Array.IndexOf(ClientStages, stage);
        if (index >= 0) Interlocked.Increment(ref _clientStages[index]);
    }

    public string Render(SignalRegistrySnapshot snapshot)
    {
        var builder = new StringBuilder(2048);
        Gauge(builder, "codex_bridge_signal_uptime_seconds", (long)(timeProvider.GetUtcNow() - _startedAt).TotalSeconds);
        Gauge(builder, "codex_bridge_signal_active_hosts", snapshot.Hosts);
        Gauge(builder, "codex_bridge_signal_active_clients", snapshot.Clients);
        Gauge(builder, "codex_bridge_signal_pairing_routes", snapshot.PairingRoutes);
        Counter(builder, "codex_bridge_signal_auth_total", "role", "host", Volatile.Read(ref _authenticatedHosts));
        Counter(builder, "codex_bridge_signal_auth_total", "role", "client", Volatile.Read(ref _authenticatedClients));
        Counter(builder, "codex_bridge_signal_auth_rejected_total", Volatile.Read(ref _authRejected));
        Counter(builder, "codex_bridge_turn_credentials_issued_total", Volatile.Read(ref _turnCredentials));
        Counter(builder, "codex_bridge_signal_forward_total", "result", "delivered", Volatile.Read(ref _forwarded));
        Counter(builder, "codex_bridge_signal_forward_total", "result", "offline", Volatile.Read(ref _forwardMisses));
        Counter(builder, "codex_bridge_remote_sessions_total", "outcome", "connected", Volatile.Read(ref _telemetryConnected));
        Counter(builder, "codex_bridge_remote_sessions_total", "outcome", "failed", Volatile.Read(ref _telemetryFailed));
        foreach (var (type, index) in new[] { ("host", 0), ("srflx", 1), ("relay", 2), ("unknown", 3) })
            Counter(builder, "codex_bridge_remote_candidate_total", "type", type, Volatile.Read(ref _candidateTypes[index]));
        for (var index = 0; index < ClientStages.Length; index++)
            Counter(builder, "codex_bridge_client_stage_total", "stage", ClientStages[index], Volatile.Read(ref _clientStages[index]));
        lock (_versionGate)
            foreach (var item in _versions.OrderBy(item => item.Key, StringComparer.Ordinal))
                Counter(builder, "codex_bridge_remote_version_total", "version", item.Key, item.Value);
        return builder.ToString();
    }

    private static string NormalizeVersion(string? value) =>
        Version.TryParse(value, out var version) && version.Major is >= 0 and <= 999 && version.Minor is >= 0 and <= 999
            ? $"{version.Major}.{version.Minor}"
            : "unknown";
    private static void Gauge(StringBuilder builder, string name, long value) =>
        builder.Append(name).Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
    private static void Counter(StringBuilder builder, string name, long value) => Gauge(builder, name, value);
    private static void Counter(StringBuilder builder, string name, string label, string labelValue, long value) =>
        builder.Append(name).Append('{').Append(label).Append("=\"").Append(labelValue).Append("\"} ")
            .Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
}
