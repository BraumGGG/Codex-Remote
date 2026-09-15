namespace CodexBridge.Host.Contracts;

public sealed record ManagedDeviceDto(
    string DeviceId,
    string DisplayName,
    string Transport,
    bool CanSend,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt);
