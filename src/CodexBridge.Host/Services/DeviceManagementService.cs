using CodexBridge.Host.Contracts;
using CodexBridge.Host.Remote;

namespace CodexBridge.Host.Services;

public sealed class DeviceManagementService(IServiceProvider services)
{
    public IReadOnlyList<ManagedDeviceDto> List()
    {
        var remoteDevices = services.GetService<RemoteDeviceStore>();
        return (remoteDevices?.List() ?? [])
            .Select(device => new ManagedDeviceDto(
                device.DeviceId,
                device.DisplayName,
                "remote",
                device.CanSend,
                device.CreatedAt,
                device.LastSeenAt))
            .OrderByDescending(device => device.LastSeenAt)
            .ThenBy(device => device.DeviceId, StringComparer.Ordinal)
            .ToArray();
    }

    public bool Revoke(string transport, string deviceId) =>
        transport == "remote" &&
        (services.GetService<RemoteDeviceStore>()?.Revoke(deviceId) ?? false);

    public bool SetCanSend(string transport, string deviceId, bool canSend) =>
        transport == "remote" &&
        (services.GetService<RemoteDeviceStore>()?.SetCanSend(deviceId, canSend) ?? false);
}
