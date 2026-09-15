namespace CodexBridge.Host.Remote;

public sealed class RemoteSessionLimitException : Exception
{
    public RemoteSessionLimitException() : base("remote_session_limit_reached") { }
}

public sealed class RemoteSessionConcurrencyGate
{
    private readonly object _gate = new();
    private readonly int _maximumDevices;
    private readonly Dictionary<string, Entry> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Entry>> _candidates = new(StringComparer.Ordinal);

    public RemoteSessionConcurrencyGate(int maximumDevices = 3)
    {
        if (maximumDevices is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maximumDevices));
        _maximumDevices = maximumDevices;
    }

    public RemoteSessionConcurrencyLease Acquire(string deviceId, CancellationToken cancellationToken)
    {
        return AcquireCore(deviceId, cancellationToken, out _);
    }

    // A reconnect must not evict a healthy session until the replacement has
    // completed ICE/transport setup. The caller commits only after success.
    public RemoteSessionConcurrencyLease AcquireCandidate(
        string deviceId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (_gate)
        {
            if (!_active.ContainsKey(deviceId) &&
                !_candidates.ContainsKey(deviceId) &&
                _active.Count + _candidates.Count >= _maximumDevices)
                throw new RemoteSessionLimitException();

            var entry = CreateEntry(cancellationToken);
            if (!_candidates.TryGetValue(deviceId, out var candidates))
                _candidates[deviceId] = candidates = [];
            candidates.Add(entry);
            return CreateLease(deviceId, entry, committed: false);
        }
    }

    public void CommitCandidate(RemoteSessionConcurrencyLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_gate)
        {
            if (!lease.TryMarkCommitted()) return;
            if (!_candidates.TryGetValue(lease.DeviceId, out var candidates) ||
                !candidates.Remove(lease.Entry))
                return;
            if (candidates.Count == 0) _candidates.Remove(lease.DeviceId);
            if (_active.TryGetValue(lease.DeviceId, out var previous))
                previous.Cancellation.Cancel();
            _active[lease.DeviceId] = lease.Entry;
        }
    }

    public async Task<RemoteSessionConcurrencyLease> AcquireAfterPreviousAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        var lease = AcquireCore(deviceId, cancellationToken, out var previousCompletion);
        if (previousCompletion is not null)
        {
            try
            {
                await previousCompletion.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
        return lease;
    }

    private RemoteSessionConcurrencyLease AcquireCore(
        string deviceId,
        CancellationToken cancellationToken,
        out Task? previousCompletion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (_gate)
        {
            previousCompletion = null;
            if (_active.TryGetValue(deviceId, out var previous))
            {
                previous.Cancellation.Cancel();
                previousCompletion = previous.Completion.Task;
            }
            else if (_active.Count >= _maximumDevices)
            {
                throw new RemoteSessionLimitException();
            }
            var generation = Guid.NewGuid();
            var entry = CreateEntry(cancellationToken, generation);
            _active[deviceId] = entry;
            return CreateLease(deviceId, entry, committed: true);
        }
    }

    private static Entry CreateEntry(CancellationToken cancellationToken, Guid? generation = null) =>
        new(
            generation ?? Guid.NewGuid(),
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    private RemoteSessionConcurrencyLease CreateLease(string deviceId, Entry entry, bool committed) =>
        new(
            deviceId,
            entry,
            committed,
            () => Release(deviceId, entry));

    private void Release(string deviceId, Entry entry)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(deviceId, out var current) && ReferenceEquals(current, entry))
                _active.Remove(deviceId);
            if (_candidates.TryGetValue(deviceId, out var candidates) && candidates.Remove(entry))
            {
                if (candidates.Count == 0) _candidates.Remove(deviceId);
            }
        }
        entry.Cancellation.Dispose();
        entry.Completion.TrySetResult();
    }

    public sealed record Entry(
        Guid Generation,
        CancellationTokenSource Cancellation,
        TaskCompletionSource Completion);
}

public sealed class RemoteSessionConcurrencyLease(
    string deviceId,
    RemoteSessionConcurrencyGate.Entry entry,
    bool committed,
    Action release) : IDisposable
{
    private Action? _release = release;
    private int _committed = committed ? 1 : 0;
    internal string DeviceId { get; } = deviceId;
    internal RemoteSessionConcurrencyGate.Entry Entry { get; } = entry;
    public CancellationToken Token => Entry.Cancellation.Token;
    internal bool TryMarkCommitted() => Interlocked.Exchange(ref _committed, 1) == 0;
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
