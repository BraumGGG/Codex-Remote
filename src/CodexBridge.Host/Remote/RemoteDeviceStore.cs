using System.Security.Cryptography;
using System.Text.Json;
using CodexBridge.Host.Auth;

namespace CodexBridge.Host.Remote;

public sealed record RemoteDevice(
    string DeviceId,
    string DisplayName,
    string PublicKeySpki,
    bool CanSend,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    string? InstallationId = null);

public sealed record RemoteDeviceRegistration(
    DevicePrincipal Principal,
    bool Created,
    RemoteDevice? ReplacedDevice = null);

public sealed class RemoteDeviceStore
{
    private readonly string _path;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private List<RemoteDevice> _devices;
    private readonly IRemoteCapabilityResolver? _capabilities;

    public event Action<string>? DeviceRevoked;

    public RemoteDeviceStore(
        string path,
        TimeProvider timeProvider,
        IRemoteCapabilityResolver? capabilities = null)
    {
        _path = Path.GetFullPath(path);
        _timeProvider = timeProvider;
        _capabilities = capabilities;
        _devices = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<RemoteDevice>>(File.ReadAllText(_path))
                ?? throw new InvalidDataException("Remote device store is invalid.")
            : [];
    }

    public DevicePrincipal Register(string displayName, string publicKeySpki, bool canSend)
    {
        var registration = RegisterForPairing(displayName, publicKeySpki, canSend);
        if (!registration.Created)
            throw new InvalidOperationException("Remote device is already registered.");
        return registration.Principal;
    }

    public RemoteDeviceRegistration RegisterForPairing(
        string displayName,
        string publicKeySpki,
        bool canSend,
        string? installationId = null)
    {
        var name = displayName?.Trim() ?? "";
        if (name.Length is < 1 or > 80) throw new ArgumentException("Device name is invalid.", nameof(displayName));
        var keyBytes = ValidatePublicKey(publicKeySpki);
        var deviceId = RemoteEncoding.Base64UrlEncode(SHA256.HashData(keyBytes));
        var normalizedInstallationId = ValidateInstallationId(installationId);
        string? revokedDeviceId = null;
        RemoteDeviceRegistration registration;
        lock (_gate)
        {
            var existing = _devices.FirstOrDefault(device => device.DeviceId == deviceId);
            if (existing is not null)
            {
                if (!string.Equals(existing.PublicKeySpki, publicKeySpki, StringComparison.Ordinal))
                    throw new InvalidDataException("Remote device identity is inconsistent.");
                // Pairing is an explicit re-authorization. Promote legacy read-only
                // records when the pairing flow grants send access, so rescanning
                // with the same mobile identity does not keep the stale flag.
                var updated = existing with
                {
                    CanSend = existing.CanSend || canSend,
                    LastSeenAt = _timeProvider.GetUtcNow(),
                    InstallationId = existing.InstallationId ?? normalizedInstallationId,
                };
                if (updated != existing)
                {
                    var index = _devices.IndexOf(existing);
                    _devices[index] = updated;
                    try { Persist(); }
                    catch { _devices[index] = existing; throw; }
                    existing = updated;
                }
                registration = new RemoteDeviceRegistration(Resolve(existing), Created: false);
            }
            else
            {
                var replacement = normalizedInstallationId is null
                    ? null
                    : _devices.FirstOrDefault(device => device.InstallationId == normalizedInstallationId);
                var now = _timeProvider.GetUtcNow();
                if (replacement is not null)
                {
                    var index = _devices.IndexOf(replacement);
                    var updated = replacement with
                    {
                        DeviceId = deviceId,
                        DisplayName = name,
                        PublicKeySpki = publicKeySpki,
                        CanSend = replacement.CanSend || canSend,
                        LastSeenAt = now,
                        InstallationId = normalizedInstallationId,
                    };
                    _devices[index] = updated;
                    try { Persist(); }
                    catch { _devices[index] = replacement; throw; }
                    registration = new RemoteDeviceRegistration(Resolve(updated), Created: false, replacement);
                    revokedDeviceId = replacement.DeviceId;
                }
                else
                {
                    var device = new RemoteDevice(deviceId, name, publicKeySpki, canSend, now, now, normalizedInstallationId);
                    _devices.Add(device);
                    try { Persist(); }
                    catch { _devices.Remove(device); throw; }
                    registration = new RemoteDeviceRegistration(Resolve(device), Created: true);
                }
            }
        }
        if (revokedDeviceId is not null) DeviceRevoked?.Invoke(revokedDeviceId);
        return registration;
    }

    public void RollbackPairing(RemoteDeviceRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        string? revokedDeviceId = null;
        lock (_gate)
        {
            var index = _devices.FindIndex(device => device.DeviceId == registration.Principal.DeviceId);
            if (index < 0) return;
            var current = _devices[index];
            if (registration.Created)
            {
                _devices.RemoveAt(index);
                try { Persist(); }
                catch { _devices.Insert(index, current); throw; }
                revokedDeviceId = current.DeviceId;
            }
            else if (registration.ReplacedDevice is not null)
            {
                _devices[index] = registration.ReplacedDevice;
                try { Persist(); }
                catch { _devices[index] = current; throw; }
                revokedDeviceId = current.DeviceId;
            }
        }
        if (revokedDeviceId is not null) DeviceRevoked?.Invoke(revokedDeviceId);
    }

    public DevicePrincipal? Authenticate(string deviceId)
    {
        lock (_gate)
        {
            var index = _devices.FindIndex(device => device.DeviceId == deviceId);
            if (index < 0) return null;
            var device = _devices[index] with { LastSeenAt = _timeProvider.GetUtcNow() };
            _devices[index] = device;
            Persist();
            var principal = new DevicePrincipal(device.DeviceId, device.DisplayName, device.CanSend);
            return _capabilities?.Resolve(principal) ?? principal;
        }
    }

    public RemoteDevice? Find(string deviceId)
    {
        lock (_gate) return _devices.FirstOrDefault(device => device.DeviceId == deviceId);
    }

    public bool SetCanSend(string deviceId, bool canSend)
    {
        lock (_gate)
        {
            var index = _devices.FindIndex(device => device.DeviceId == deviceId);
            if (index < 0) return false;
            var previous = _devices[index];
            if (previous.CanSend == canSend) return true;
            _devices[index] = previous with { CanSend = canSend };
            try { Persist(); }
            catch { _devices[index] = previous; throw; }
            return true;
        }
    }

    public IReadOnlyList<RemoteDevice> List()
    {
        lock (_gate)
        {
            return _devices
                .OrderByDescending(device => device.LastSeenAt)
                .ThenBy(device => device.DeviceId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public bool Revoke(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        lock (_gate)
        {
            var index = _devices.FindIndex(device => device.DeviceId == deviceId);
            if (index < 0) return false;
            var removed = _devices[index];
            _capabilities?.Remove(deviceId);
            _devices.RemoveAt(index);
            try
            {
                Persist();
            }
            catch
            {
                _devices.Insert(index, removed);
                throw;
            }
        }

        DeviceRevoked?.Invoke(deviceId);
        return true;
    }

    private void Persist()
    {
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Remote device path has no parent.");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(_devices));
            File.Move(temp, _path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static byte[] ValidatePublicKey(string publicKeySpki)
    {
        try
        {
            var bytes = RemoteEncoding.Base64UrlDecode(publicKeySpki);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read != bytes.Length || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
                throw new InvalidDataException("Remote device key is invalid.");
            return bytes;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or ArgumentException)
        {
            throw new InvalidDataException("Remote device key is invalid.", exception);
        }
    }

    private static string? ValidateInstallationId(string? installationId)
    {
        if (installationId is null) return null;
        if (!System.Text.RegularExpressions.Regex.IsMatch(installationId, "^[A-Za-z0-9_-]{43}$"))
            throw new ArgumentException("Installation identity is invalid.", nameof(installationId));
        return installationId;
    }

    private DevicePrincipal Resolve(RemoteDevice device)
    {
        var principal = new DevicePrincipal(device.DeviceId, device.DisplayName, device.CanSend);
        return _capabilities?.Resolve(principal) ?? principal;
    }
}
