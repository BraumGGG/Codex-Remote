using System.Collections.Concurrent;

namespace CodexBridge.Signal;

public sealed record SignalConnection(string Id, string IpAddress);
public sealed record SignalRegistrySnapshot(int Hosts, int Clients, int PairingRoutes);

public sealed class SignalConnectionRegistry(TimeProvider timeProvider, int maximumConnectionsPerIp = 10)
{
    private static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, SignalConnection> _hosts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SignalConnection> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PairingRoute> _pairingRoutes = new(StringComparer.Ordinal);
    private readonly object _connectionGate = new();
    private readonly object _pairingGate = new();

    public bool TryAddHost(string hostId, SignalConnection connection)
    {
        lock (_connectionGate)
            return HasCapacity(connection.IpAddress) && _hosts.TryAdd(hostId, connection);
    }

    public bool TryReplaceHost(string hostId, SignalConnection connection, out SignalConnection? replaced)
    {
        lock (_connectionGate)
        {
            if (_hosts.TryGetValue(hostId, out replaced))
            {
                _hosts[hostId] = connection;
                return true;
            }
            if (!HasCapacity(connection.IpAddress)) return false;
            replaced = null;
            return _hosts.TryAdd(hostId, connection);
        }
    }

    public bool TryAddClient(string hostId, string deviceId, SignalConnection connection)
    {
        lock (_connectionGate)
            return HasCapacity(connection.IpAddress) && _clients.TryAdd(ClientKey(hostId, deviceId), connection);
    }


    public bool TryReplaceClient(
        string hostId, string deviceId, SignalConnection connection, out SignalConnection? replaced)
    {
        lock (_connectionGate)
        {
            var key = ClientKey(hostId, deviceId);
            if (_clients.TryGetValue(key, out replaced))
            {
                _clients[key] = connection;
                return true;
            }
            if (!HasCapacity(connection.IpAddress)) return false;
            replaced = null;
            return _clients.TryAdd(key, connection);
        }
    }

    public void RemoveHost(string hostId)
    {
        lock (_connectionGate) _hosts.TryRemove(hostId, out _);
    }
    public bool RemoveHost(string hostId, string connectionId)
    {
        lock (_connectionGate)
            return _hosts.TryGetValue(hostId, out var current) && current.Id == connectionId &&
                   _hosts.TryRemove(new KeyValuePair<string, SignalConnection>(hostId, current));
    }
    public void RemoveClient(string hostId, string deviceId)
    {
        lock (_connectionGate) _clients.TryRemove(ClientKey(hostId, deviceId), out _);
    }
    public bool RemoveClient(string hostId, string deviceId, string connectionId)
    {
        lock (_connectionGate)
        {
            var key = ClientKey(hostId, deviceId);
            return _clients.TryGetValue(key, out var current) && current.Id == connectionId &&
                   _clients.TryRemove(new KeyValuePair<string, SignalConnection>(key, current));
        }
    }
    public SignalConnection? GetHost(string hostId) => _hosts.GetValueOrDefault(hostId);
    public SignalConnection? GetClient(string hostId, string deviceId) => _clients.GetValueOrDefault(ClientKey(hostId, deviceId));
    public SignalRegistrySnapshot Snapshot()
    {
        PrunePairingRoutes();
        return new SignalRegistrySnapshot(_hosts.Count, _clients.Count, _pairingRoutes.Count);
    }

    public bool TryAddPairingRoute(string routeId, string hostId, DateTimeOffset expiresAt)
    {
        lock (_pairingGate)
        {
            PrunePairingRoutes();
            var now = timeProvider.GetUtcNow();
            if (string.IsNullOrWhiteSpace(routeId) || !_hosts.ContainsKey(hostId) ||
                expiresAt <= now || expiresAt > now + PairingLifetime)
                return false;
            if (_pairingRoutes.TryGetValue(routeId, out var existing) && existing.HostId != hostId)
                return false;

            foreach (var previous in _pairingRoutes
                         .Where(item => item.Value.HostId == hostId && item.Key != routeId)
                         .Select(item => item.Key)
                         .ToArray())
                _pairingRoutes.TryRemove(previous, out _);
            _pairingRoutes[routeId] = new PairingRoute(hostId, expiresAt);
            return true;
        }
    }

    public string? ResolvePairingRoute(string routeId)
    {
        PrunePairingRoutes();
        return _pairingRoutes.TryGetValue(routeId, out var route) ? route.HostId : null;
    }

    private bool HasCapacity(string ipAddress)
    {
        var count = _hosts.Values.Count(item => item.IpAddress == ipAddress) +
                    _clients.Values.Count(item => item.IpAddress == ipAddress);
        return count < maximumConnectionsPerIp;
    }

    private void PrunePairingRoutes()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var route in _pairingRoutes.Where(item => item.Value.ExpiresAt <= now).ToArray())
            _pairingRoutes.TryRemove(route.Key, out _);
    }

    private static string ClientKey(string hostId, string deviceId) => $"{hostId}\0{deviceId}";
    private sealed record PairingRoute(string HostId, DateTimeOffset ExpiresAt);
}
