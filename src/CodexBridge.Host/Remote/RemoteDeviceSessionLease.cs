namespace CodexBridge.Host.Remote;

public sealed class RemoteDeviceSessionLease : IDisposable
{
    private readonly RemoteDeviceStore _store;
    private readonly string _deviceId;
    private readonly CancellationTokenSource _cancellation;
    private readonly object _gate = new();
    private bool _disposed;

    public RemoteDeviceSessionLease(
        RemoteDeviceStore store,
        string deviceId,
        CancellationToken parentCancellation)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        _deviceId = deviceId;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(parentCancellation);
        _store.DeviceRevoked += OnDeviceRevoked;
        if (_store.Find(deviceId) is null)
        {
            _cancellation.Cancel();
        }
    }

    public CancellationToken Token => _cancellation.Token;

    private void OnDeviceRevoked(string deviceId)
    {
        if (string.Equals(deviceId, _deviceId, StringComparison.Ordinal))
        {
            lock (_gate)
            {
                if (!_disposed) _cancellation.Cancel();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _store.DeviceRevoked -= OnDeviceRevoked;
            _cancellation.Dispose();
        }
    }
}
