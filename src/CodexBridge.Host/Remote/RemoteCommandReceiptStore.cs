using System.Text.Json;

namespace CodexBridge.Host.Remote;

public enum RemoteCommandState
{
    Accepted,
    Completed,
    Rejected,
    Unknown,
}

public sealed record RemoteCommandReceipt(
    Guid CommandId,
    string DeviceId,
    string ThreadId,
    string TextSha256,
    RemoteCommandState State,
    string? ErrorCode,
    DateTimeOffset UpdatedAt);

public sealed record BeginRemoteCommandResult(bool IsNew, RemoteCommandReceipt Receipt);

public sealed class RemoteCommandReceiptStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<Guid, RemoteCommandReceipt> _receipts;

    public RemoteCommandReceiptStore(string path, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _receipts = Load(_path).ToDictionary(receipt => receipt.CommandId);
        foreach (var (id, receipt) in _receipts.ToArray())
        {
            if (receipt.State == RemoteCommandState.Accepted)
                _receipts[id] = receipt with { State = RemoteCommandState.Unknown };
        }
    }

    public async Task<BeginRemoteCommandResult> BeginAsync(
        Guid commandId,
        string deviceId,
        string threadId,
        string textSha256,
        CancellationToken cancellationToken = default)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("Command ID is required.", nameof(commandId));
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        if (textSha256 is null || textSha256.Length != 64 ||
            textSha256.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Text digest is invalid.", nameof(textSha256));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_receipts.TryGetValue(commandId, out var existing))
            {
                if (!string.Equals(existing.DeviceId, deviceId, StringComparison.Ordinal) ||
                    !string.Equals(existing.ThreadId, threadId, StringComparison.Ordinal) ||
                    !string.Equals(existing.TextSha256, textSha256, StringComparison.Ordinal))
                {
                    return new BeginRemoteCommandResult(false, existing with
                    {
                        State = RemoteCommandState.Rejected,
                        ErrorCode = "command_id_conflict",
                    });
                }
                return new BeginRemoteCommandResult(false, existing);
            }

            var receipt = new RemoteCommandReceipt(
                commandId,
                deviceId,
                threadId,
                textSha256,
                RemoteCommandState.Accepted,
                null,
                _timeProvider.GetUtcNow());
            _receipts.Add(commandId, receipt);
            try { await PersistAsync(cancellationToken).ConfigureAwait(false); }
            catch
            {
                _receipts.Remove(commandId);
                throw;
            }
            return new BeginRemoteCommandResult(true, receipt);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<RemoteCommandReceipt> CompleteAsync(Guid commandId, CancellationToken cancellationToken = default) =>
        UpdateAsync(commandId, RemoteCommandState.Completed, null, cancellationToken);

    public Task<RemoteCommandReceipt> RejectAsync(
        Guid commandId,
        string errorCode,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(commandId, RemoteCommandState.Rejected, errorCode, cancellationToken);

    public Task<RemoteCommandReceipt> MarkUnknownAsync(
        Guid commandId,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(commandId, RemoteCommandState.Unknown, "send_result_unknown", cancellationToken);

    private async Task<RemoteCommandReceipt> UpdateAsync(
        Guid commandId,
        RemoteCommandState state,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_receipts.TryGetValue(commandId, out var existing))
                throw new KeyNotFoundException("Command receipt does not exist.");
            var updated = existing with
            {
                State = state,
                ErrorCode = errorCode,
                UpdatedAt = _timeProvider.GetUtcNow(),
            };
            _receipts[commandId] = updated;
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Receipt store path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(_receipts.Values.OrderBy(item => item.UpdatedAt));
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static IReadOnlyList<RemoteCommandReceipt> Load(string path)
    {
        if (!File.Exists(path)) return [];
        return JsonSerializer.Deserialize<List<RemoteCommandReceipt>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Command receipt store is invalid.");
    }
}
