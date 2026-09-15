using System.Text.Json.Serialization;

namespace CodexBridge.Host.Diagnostics;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BridgeComponentState
{
    Disabled,
    Starting,
    Online,
    Degraded,
    Offline,
    CircuitOpen,
}

public sealed record BridgeComponentHealth(
    BridgeComponentState State,
    DateTimeOffset ChangedAt,
    string? ErrorCode);

public sealed record BridgeHealthSnapshot(
    DateTimeOffset CapturedAt,
    BridgeComponentHealth Host,
    BridgeComponentHealth Signal,
    BridgeComponentHealth Turn,
    BridgeComponentHealth Sidecar,
    BridgeComponentHealth Pairing,
    BridgeComponentHealth Desktop);

public enum BridgeComponent
{
    Host,
    Signal,
    Turn,
    Sidecar,
    Pairing,
}
