using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexBridge.Core;

namespace CodexBridge.Windows;

public sealed class RolloutIndexStore : IRolloutIndexStore
{
    private const int SchemaVersion = 1;
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, StoredIndex> _cache = new(StringComparer.OrdinalIgnoreCase);

    public RolloutIndexStore(string directory)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        Directory.CreateDirectory(_directory);
    }

    public async Task<RolloutIndexSummary> GetSummaryAsync(
        string rolloutPath,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await EnsureIndexAsync(rolloutPath, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<(long Sequence, ConversationEvent Event)>> GetPageAsync(
        string rolloutPath,
        long beforeSequence,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var summary = await GetSummaryAsync(rolloutPath, cancellationToken).ConfigureAwait(false);
        var selected = summary.Entries
            .Where(entry => entry.Sequence < beforeSequence)
            .TakeLast(pageSize)
            .ToArray();
        return await ReadEntriesAsync(rolloutPath, selected, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConversationEvent?> GetEventAsync(
        string rolloutPath,
        long sequence,
        CancellationToken cancellationToken = default)
    {
        var summary = await GetSummaryAsync(rolloutPath, cancellationToken).ConfigureAwait(false);
        var entry = summary.Entries.FirstOrDefault(value => value.Sequence == sequence);
        if (entry is null) return null;
        return (await ReadEntriesAsync(rolloutPath, [entry], cancellationToken).ConfigureAwait(false))[0].Event;
    }

    private async Task<RolloutIndexSummary> EnsureIndexAsync(string rolloutPath, CancellationToken cancellationToken)
    {
        var info = new FileInfo(rolloutPath);
        if (!info.Exists) throw new FileNotFoundException("Rollout file was not found.", rolloutPath);
        var identity = CreateIdentity(info);
        var indexPath = GetIndexPath(rolloutPath);
        StoredIndex? stored = null;
        var changed = false;
        var normalizedPath = Path.GetFullPath(rolloutPath);
        if (_cache.TryGetValue(normalizedPath, out var cached) &&
            cached.Identity == identity && cached.IndexedLength <= info.Length)
        {
            stored = cached;
        }
        try
        {
            if (stored is null && File.Exists(indexPath))
                stored = JsonSerializer.Deserialize<StoredIndex>(await File.ReadAllBytesAsync(indexPath, cancellationToken));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            stored = null;
        }
        if (stored is null || stored.Schema != SchemaVersion || stored.Identity != identity ||
            stored.IndexedLength < 0 || stored.IndexedLength > info.Length)
        {
            stored = new StoredIndex(SchemaVersion, identity, 0, [], "idle");
            changed = true;
        }
        if (stored.IndexedLength < info.Length)
        {
            stored = await AppendAsync(rolloutPath, stored, info.Length, cancellationToken).ConfigureAwait(false);
            changed = true;
        }
        if (changed) await SaveAsync(indexPath, stored, cancellationToken).ConfigureAwait(false);
        _cache[normalizedPath] = stored;
        return new RolloutIndexSummary(
            stored.Identity,
            info.Length,
            stored.IndexedLength,
            stored.Entries.Count,
            stored.Status,
            stored.Entries);
    }

    private static async Task<StoredIndex> AppendAsync(
        string path,
        StoredIndex stored,
        long sourceLength,
        CancellationToken cancellationToken)
    {
        var entries = stored.Entries.ToList();
        var status = stored.Status;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        stream.Position = stored.IndexedLength;
        var buffer = new byte[64 * 1024];
        var pending = new List<byte>();
        var lineOffset = stored.IndexedLength;
        long consumed = stored.IndexedLength;
        while (consumed < sourceLength)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, sourceLength - consumed)), cancellationToken);
            if (count == 0) break;
            for (var index = 0; index < count; index++)
            {
                var value = buffer[index];
                consumed++;
                if (value != (byte)'\n') { pending.Add(value); continue; }
                var length = checked((int)(consumed - lineOffset));
                var textLength = pending.Count > 0 && pending[^1] == (byte)'\r' ? pending.Count - 1 : pending.Count;
                ConversationEvent? parsed = null;
                try
                {
                    var line = Encoding.UTF8.GetString(pending.GetRange(0, textLength).ToArray());
                    parsed = RolloutConversationReader.ParseVisibleEvent(line.TrimStart('\uFEFF'));
                }
                catch (JsonException) { }
                if (parsed is not null)
                {
                    entries.Add(new RolloutIndexEntry(entries.Count + 1L, lineOffset, length, parsed.Kind, parsed.Timestamp));
                    if (parsed.Kind == ConversationEventKind.TaskStarted) status = "running";
                    if (parsed.Kind == ConversationEventKind.TaskCompleted) status = "idle";
                }
                pending.Clear();
                lineOffset = consumed;
            }
        }
        return new StoredIndex(SchemaVersion, stored.Identity, lineOffset, entries, status);
    }

    private static async Task<IReadOnlyList<(long Sequence, ConversationEvent Event)>> ReadEntriesAsync(
        string path,
        IReadOnlyList<RolloutIndexEntry> entries,
        CancellationToken cancellationToken)
    {
        var result = new List<(long, ConversationEvent)>(entries.Count);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
        foreach (var entry in entries)
        {
            stream.Position = entry.Offset;
            var bytes = new byte[entry.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            var length = bytes.Length;
            while (length > 0 && bytes[length - 1] is (byte)'\n' or (byte)'\r') length--;
            var parsed = RolloutConversationReader.ParseVisibleEvent(
                Encoding.UTF8.GetString(bytes, 0, length).TrimStart('\uFEFF'));
            if (parsed is null) throw new InvalidDataException("Indexed rollout entry is no longer visible.");
            result.Add((entry.Sequence, parsed));
        }
        return result;
    }

    private async Task SaveAsync(string path, StoredIndex value, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(value), cancellationToken);
        File.Move(temporary, path, true);
    }

    private string GetIndexPath(string rolloutPath) => Path.Combine(_directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(rolloutPath).ToUpperInvariant())).AsSpan(0, 16)).ToLowerInvariant() + ".json");

    private static string CreateIdentity(FileInfo info) =>
        $"{Path.GetFullPath(info.FullName).ToUpperInvariant()}:{info.CreationTimeUtc.Ticks:x16}";

    private sealed record StoredIndex(
        int Schema,
        string Identity,
        long IndexedLength,
        List<RolloutIndexEntry> Entries,
        string Status);
}
