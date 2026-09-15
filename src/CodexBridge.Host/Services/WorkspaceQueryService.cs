using System.Security.Cryptography;
using System.Text;
using CodexBridge.Core;
using CodexBridge.Host.Contracts;
using CodexBridge.Remote.Protocol;
using CodexBridge.Windows;

namespace CodexBridge.Host.Services;

public sealed class WorkspaceQueryService
{
    public const int InlineEventTextBytes = 8 * 1024;
    public const int MaximumEventTextBytes = 4 * 1024 * 1024;
    private const int ThreadPreviewBytes = 768;
    private readonly IThreadCatalog _catalog;
    private readonly IConversationReader _reader;
    private readonly TargetPolicy _policy;
    private readonly TextFileAttachmentService? _fileAttachments;
    private readonly PagingCursorCodec _cursors;
    private readonly IRolloutIndexStore? _index;

    public WorkspaceQueryService(
        IThreadCatalog catalog,
        IConversationReader reader,
        TargetPolicy policy,
        TextFileAttachmentService? fileAttachments = null,
        PagingCursorCodec? cursors = null,
        IRolloutIndexStore? index = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _fileAttachments = fileAttachments;
        _cursors = cursors ?? new PagingCursorCodec();
        _index = index;
    }

    public async Task<IReadOnlyList<ProjectDto>> GetProjectsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<ProjectDto>();
        foreach (var projectPath in _policy.GetAllowedProjectPaths())
        {
            var threads = await _catalog.ListByProjectAsync(projectPath, cancellationToken);
            result.Add(new ProjectDto(
                CreateProjectId(projectPath),
                Path.GetFileName(projectPath),
                threads.Count,
                threads.Count == 0 ? 0 : threads.Max(thread => thread.UpdatedAtMs),
                0,
                "building",
                _policy.CanSend(projectPath)));
        }
        return result;
    }

    public async Task<ThreadPageDto> GetThreadsAsync(
        string projectId,
        string? cursor = null,
        int pageSize = 30,
        string? query = null,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 10 or > 50) throw new RemoteProtocolException("invalid_parameters", "Page size is invalid.");
        query = PagingCursorCodec.NormalizeQuery(query);
        if (query.Length > 120) throw new RemoteProtocolException("invalid_parameters", "Query is too long.");
        var projectPath = _policy.GetAllowedProjectPaths().FirstOrDefault(path =>
            string.Equals(CreateProjectId(path), projectId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException("项目不存在。");
        var threads = await _catalog.ListByProjectAsync(projectPath, cancellationToken);
        var filtered = threads
            .Where(thread => query.Length == 0 ||
                thread.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                thread.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                thread.Preview.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(thread => thread.UpdatedAtMs)
            .ThenBy(thread => thread.Id, StringComparer.Ordinal)
            .ToList();
        var totalApproximate = filtered.Count;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            var anchor = _cursors.DecodeThreadCursor(cursor, projectId, query);
            filtered = filtered.Where(thread =>
                thread.UpdatedAtMs < anchor.UpdatedAtMs ||
                (thread.UpdatedAtMs == anchor.UpdatedAtMs &&
                 string.CompareOrdinal(thread.Id, anchor.ThreadId) > 0)).ToList();
        }
        var page = filtered.Take(pageSize + 1).ToList();
        var hasMore = page.Count > pageSize;
        if (hasMore) page.RemoveAt(page.Count - 1);
        var items = new List<ThreadDto>(page.Count);
        foreach (var thread in page)
        {
            var status = "idle";
            var preview = thread.Preview;
            if (_index is not null)
            {
                try
                {
                    var summary = await _index.GetSummaryAsync(thread.RolloutPath, cancellationToken)
                        .ConfigureAwait(false);
                    status = summary.Status;
                    preview = await GetLatestPreviewAsync(thread, summary.LatestSequence, cancellationToken)
                        .ConfigureAwait(false) ?? preview;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    status = "idle";
                }
            }
            else
            {
                preview = await GetLatestPreviewAsync(thread, cancellationToken).ConfigureAwait(false) ?? preview;
            }
            items.Add(new ThreadDto(
                thread.Id,
                thread.Title,
                TrimPreview(preview),
                thread.UpdatedAtMs,
                status,
                string.IsNullOrWhiteSpace(thread.DisplayName) ? thread.Title : thread.DisplayName));
        }
        // Keep the page envelope below the remote 48 KiB frame limit even
        // when individual thread previews are unusually large.
        while (items.Count > 1)
        {
            var probe = new ThreadPageDto(items, null, hasMore, totalApproximate);
            var bytes = RpcJson.Encode(new RpcResponse(
                true,
                System.Text.Json.JsonSerializer.SerializeToElement(probe, RpcJson.Options),
                null)).Length;
            if (bytes <= RpcJson.MaximumResponsePayloadLength) break;
            items.RemoveAt(items.Count - 1);
            page.RemoveAt(page.Count - 1);
        }
        hasMore = hasMore || items.Count < filtered.Count;
        var last = page.LastOrDefault();
        var nextCursor = hasMore && last is not null
            ? _cursors.EncodeThreadCursor(projectId, query, last.UpdatedAtMs, last.Id)
            : null;
        return new ThreadPageDto(items, nextCursor, hasMore, totalApproximate);
    }

    private async Task<string?> GetLatestPreviewAsync(
        ThreadSummary thread,
        long latestSequence,
        CancellationToken cancellationToken)
    {
        if (latestSequence < 1) return null;
        var values = await _index!.GetPageAsync(thread.RolloutPath, latestSequence + 1, 8, cancellationToken)
            .ConfigureAwait(false);
        return GetLatestPreviewText(thread, values.Select(value => value.Event));
    }

    private async Task<string?> GetLatestPreviewAsync(ThreadSummary thread, CancellationToken cancellationToken)
    {
        string? latest = null;
        await foreach (var value in _reader.ReadAsync(thread.RolloutPath, false, cancellationToken)
            .ConfigureAwait(false))
        {
            var text = GetLatestPreviewText(thread, [value]);
            if (!string.IsNullOrWhiteSpace(text)) latest = text;
        }
        return latest;
    }

    private string? GetLatestPreviewText(ThreadSummary thread, IEnumerable<ConversationEvent> events)
    {
        foreach (var value in events.Reverse())
        {
            if (value.Kind == ConversationEventKind.Unknown || string.IsNullOrWhiteSpace(value.Text)) continue;
            var projection = _fileAttachments?.ProjectEvent(thread.Id, thread.Cwd, value.Kind, value.Text)
                ?? new TextFileEventProjection(value.Text, []);
            if (!string.IsNullOrWhiteSpace(projection.Text)) return projection.Text;
        }
        return null;
    }

    private static string TrimPreview(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= ThreadPreviewBytes) return value;
        return DecodeUtf8Prefix(bytes, ThreadPreviewBytes).TrimEnd() + "…";
    }

    public async Task<ConversationEventPageDto> GetEventsAsync(
        string threadId,
        string? beforeCursor = null,
        int pageSize = 40,
        int maximumBytes = RpcJson.MaximumResponsePayloadLength,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 10 or > 100 || maximumBytes is < 8 * 1024 or > RpcJson.MaximumResponsePayloadLength)
            throw new RemoteProtocolException("invalid_parameters", "Event page parameters are invalid.");

        ThreadSummary? thread;
        try { thread = await _catalog.GetAsync(threadId, cancellationToken); }
        catch (UnauthorizedAccessException) { thread = null; }
        if (thread is null)
        {
            throw new KeyNotFoundException("会话不存在。");
        }

        var rollout = GetRolloutSnapshot(thread.RolloutPath);
        if (_index is not null)
            return await GetIndexedEventPageAsync(
                thread, rollout, beforeCursor, pageSize, maximumBytes, cancellationToken).ConfigureAwait(false);
        var all = await ReadVisibleEventsAsync(
            threadId, thread.Cwd, thread.RolloutPath, rollout.Identity, cancellationToken);
        var beforeSequence = all.Count + 1L;
        if (!string.IsNullOrWhiteSpace(beforeCursor))
        {
            var cursor = _cursors.DecodeEventCursor(beforeCursor, threadId);
            if (!string.Equals(cursor.RolloutIdentity, rollout.Identity, StringComparison.Ordinal) ||
                rollout.Length < cursor.SourceLength)
                throw new RemoteProtocolException("cursor_stale", "Rollout changed after the cursor was issued.");
            beforeSequence = cursor.BeforeSequence;
        }
        var candidates = all
            .Where(item => item.Sequence < beforeSequence)
            .OrderByDescending(item => item.Sequence)
            .Take(pageSize)
            .Reverse()
            .ToList();
        while (candidates.Count > 0)
        {
            var candidateFirst = candidates[0].Sequence;
            var candidateHasMore = all.Any(item => item.Sequence < candidateFirst);
            var candidateCursor = candidateHasMore
                ? _cursors.EncodeEventCursor(threadId, rollout.Identity, rollout.Length, candidateFirst)
                : null;
            if (EncodedEventPageLength(candidates, candidateCursor, candidateHasMore, all.Count) <= maximumBytes)
                break;
            candidates.RemoveAt(0);
        }
        if (candidates.Count == 0 && all.Any(item => item.Sequence < beforeSequence))
        {
            // Never block an entire conversation because one event contains a
            // huge attachment or payload. Return a compact stub; full text
            // remains available through GetEventText when contentId exists.
            var oversized = all.Where(item => item.Sequence < beforeSequence)
                .OrderByDescending(item => item.Sequence).First();
            candidates.Add(CompactEvent(oversized));
        }
        var firstSequence = candidates.FirstOrDefault()?.Sequence ?? beforeSequence;
        var hasMoreBefore = all.Any(item => item.Sequence < firstSequence);
        var previousCursor = hasMoreBefore
            ? _cursors.EncodeEventCursor(threadId, rollout.Identity, rollout.Length, firstSequence)
            : null;
        return new ConversationEventPageDto(candidates, previousCursor, hasMoreBefore, all.Count);
    }

    public async Task<EventTextContentDto> GetEventTextAsync(
        string threadId,
        long sequence,
        string contentId,
        CancellationToken cancellationToken = default)
    {
        if (sequence < 1 || string.IsNullOrWhiteSpace(contentId))
            throw new RemoteProtocolException("invalid_parameters", "Event text parameters are invalid.");
        ThreadSummary? thread;
        try { thread = await _catalog.GetAsync(threadId, cancellationToken).ConfigureAwait(false); }
        catch (UnauthorizedAccessException) { thread = null; }
        if (thread is null) throw new KeyNotFoundException("会话不存在。");
        var rollout = GetRolloutSnapshot(thread.RolloutPath);
        var decoded = _cursors.DecodeEventContentId(contentId, threadId, sequence);
        if (!string.Equals(decoded.RolloutIdentity, rollout.Identity, StringComparison.Ordinal))
            throw new RemoteProtocolException("cursor_stale", "Rollout changed after the content ID was issued.");
        var indexedEvent = _index is null
            ? null
            : await _index.GetEventAsync(thread.RolloutPath, sequence, cancellationToken).ConfigureAwait(false);
        var text = _index is null
            ? await ReadEventTextAsync(threadId, thread.Cwd, thread.RolloutPath, sequence, cancellationToken)
            : indexedEvent is null ? null :
                (_fileAttachments?.ProjectEvent(threadId, thread.Cwd, indexedEvent.Kind, indexedEvent.Text)
                    ?? new TextFileEventProjection(indexedEvent.Text, [])).Text;
        if (text is null)
            throw new RemoteProtocolException("invalid_content_id", "Event content ID is not valid for this event.");
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(hash, decoded.TextHash, StringComparison.Ordinal))
            throw new RemoteProtocolException("invalid_content_id", "Event content changed after the content ID was issued.");
        if (bytes.Length > MaximumEventTextBytes)
            throw new RemoteProtocolException("content_too_large", "Event text exceeds its size limit.");
        return new EventTextContentDto(text, bytes.Length);
    }

    private static int EncodedEventPageLength(
        IReadOnlyList<ConversationEventDto> items,
        string? previousCursor,
        bool hasMoreBefore,
        long latestSequence) => RpcJson.Encode(new RpcResponse(
            true,
            System.Text.Json.JsonSerializer.SerializeToElement(
                new ConversationEventPageDto(items, previousCursor, hasMoreBefore, latestSequence),
                RpcJson.Options),
            null)).Length;

    internal static (string Identity, long Length) GetRolloutSnapshot(string rolloutPath)
    {
        try
        {
            var info = new FileInfo(rolloutPath);
            if (info.Exists)
                return ($"{Path.GetFullPath(rolloutPath).ToUpperInvariant()}:{info.CreationTimeUtc.Ticks:x16}", info.Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rolloutPath)).AsSpan(0, 16)), 0);
    }

    public static string CreateProjectId(string projectPath)
    {
        var normalized = TargetPolicy.NormalizePath(projectPath).ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    private async Task<List<ConversationEventDto>> ReadVisibleEventsAsync(
        string threadId,
        string projectRoot,
        string rolloutPath,
        string rolloutIdentity,
        CancellationToken cancellationToken,
        long afterSequence = 0)
    {
        var result = new List<ConversationEventDto>();
        long sequence = 0;
        await foreach (var item in _reader.ReadAsync(
                           rolloutPath,
                           follow: false,
                           cancellationToken))
        {
            if (item.Kind == ConversationEventKind.Unknown)
            {
                continue;
            }

            sequence++;
            if (sequence <= afterSequence)
            {
                continue;
            }

            var projection = _fileAttachments?.ProjectEvent(threadId, projectRoot, item.Kind, item.Text)
                ?? new TextFileEventProjection(item.Text, []);
            var text = projection.Text;
            var utf8 = text is null ? [] : Encoding.UTF8.GetBytes(text);
            // The page envelope has a 48 KiB protocol limit. A single event
            // must therefore remain compact even when its source text is huge.
            var isLarge = utf8.Length > InlineEventTextBytes;
            var preview = isLarge ? DecodeUtf8Prefix(utf8, ThreadPreviewBytes) : null;
            var contentId = isLarge
                ? _cursors.EncodeEventContentId(
                    threadId,
                    sequence,
                    rolloutIdentity,
                    Convert.ToHexString(SHA256.HashData(utf8)).ToLowerInvariant())
                : null;
            result.Add(new ConversationEventDto(
                sequence,
                item.Kind.ToString(),
                item.Timestamp,
                isLarge ? null : text,
                item.TurnId,
                ImageAttachmentService.CreateDtos(item.LocalImagePaths),
                projection.Files,
                preview,
                contentId,
                utf8.Length));
        }

        return result;
    }

    private async Task<string?> ReadEventTextAsync(
        string threadId,
        string projectRoot,
        string rolloutPath,
        long targetSequence,
        CancellationToken cancellationToken)
    {
        long sequence = 0;
        await foreach (var item in _reader.ReadAsync(rolloutPath, follow: false, cancellationToken))
        {
            if (item.Kind == ConversationEventKind.Unknown) continue;
            sequence++;
            if (sequence != targetSequence) continue;
            return (_fileAttachments?.ProjectEvent(threadId, projectRoot, item.Kind, item.Text)
                ?? new TextFileEventProjection(item.Text, [])).Text;
        }
        return null;
    }

    private async Task<ConversationEventPageDto> GetIndexedEventPageAsync(
        ThreadSummary thread,
        (string Identity, long Length) rollout,
        string? beforeCursor,
        int pageSize,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var summary = await _index!.GetSummaryAsync(thread.RolloutPath, cancellationToken).ConfigureAwait(false);
        var beforeSequence = summary.LatestSequence + 1;
        if (!string.IsNullOrWhiteSpace(beforeCursor))
        {
            var cursor = _cursors.DecodeEventCursor(beforeCursor, thread.Id);
            if (!string.Equals(cursor.RolloutIdentity, rollout.Identity, StringComparison.Ordinal) ||
                rollout.Length < cursor.SourceLength)
                throw new RemoteProtocolException("cursor_stale", "Rollout changed after the cursor was issued.");
            beforeSequence = cursor.BeforeSequence;
        }
        var values = await _index.GetPageAsync(thread.RolloutPath, beforeSequence, pageSize, cancellationToken)
            .ConfigureAwait(false);
        var result = new List<ConversationEventDto>(values.Count);
        foreach (var value in values)
        {
            var projection = _fileAttachments?.ProjectEvent(thread.Id, thread.Cwd, value.Event.Kind, value.Event.Text)
                ?? new TextFileEventProjection(value.Event.Text, []);
            var text = projection.Text;
            var utf8 = text is null ? [] : Encoding.UTF8.GetBytes(text);
            var isLarge = utf8.Length > InlineEventTextBytes;
            var preview = isLarge ? DecodeUtf8Prefix(utf8, ThreadPreviewBytes) : null;
            var contentId = isLarge
                ? _cursors.EncodeEventContentId(thread.Id, value.Sequence, rollout.Identity,
                    Convert.ToHexString(SHA256.HashData(utf8)).ToLowerInvariant())
                : null;
            result.Add(new ConversationEventDto(
                value.Sequence,
                value.Event.Kind.ToString(),
                value.Event.Timestamp,
                isLarge ? null : text,
                value.Event.TurnId,
                ImageAttachmentService.CreateDtos(value.Event.LocalImagePaths),
                projection.Files,
                preview,
                contentId,
                utf8.Length));
        }
        while (result.Count > 0)
        {
            var first = result[0].Sequence;
            var hasMore = first > 1;
            var cursor = hasMore
                ? _cursors.EncodeEventCursor(thread.Id, rollout.Identity, rollout.Length, first)
                : null;
            if (EncodedEventPageLength(result, cursor, hasMore, summary.LatestSequence) <= maximumBytes) break;
            result.RemoveAt(0);
        }
        if (result.Count == 0 && beforeSequence > 1)
        {
            var oversized = await _index.GetPageAsync(thread.RolloutPath, beforeSequence, 1, cancellationToken)
                .ConfigureAwait(false);
            if (oversized.Count > 0)
                result.Add(CompactIndexedEvent(thread, rollout, oversized[0]));
        }
        var firstSequence = result.FirstOrDefault()?.Sequence ?? beforeSequence;
        var hasMoreBefore = firstSequence > 1;
        var previousCursor = hasMoreBefore
            ? _cursors.EncodeEventCursor(thread.Id, rollout.Identity, rollout.Length, firstSequence)
            : null;
        return new ConversationEventPageDto(result, previousCursor, hasMoreBefore, summary.LatestSequence);
    }

    private static string DecodeUtf8Prefix(byte[] bytes, int maximumBytes)
    {
        var length = Math.Min(bytes.Length, maximumBytes);
        while (length > 0)
        {
            try { return new UTF8Encoding(false, true).GetString(bytes, 0, length); }
            catch (DecoderFallbackException) { length--; }
        }
        return string.Empty;
    }

    private static ConversationEventDto CompactEvent(ConversationEventDto value) =>
        value with
        {
            Text = null,
            Images = [],
            Files = [],
            TextPreview = string.IsNullOrWhiteSpace(value.TextPreview) ? "内容较大，点击加载全文" : value.TextPreview
        };

    private ConversationEventDto CompactIndexedEvent(
        ThreadSummary thread,
        (string Identity, long Length) rollout,
        (long Sequence, ConversationEvent Event) value)
    {
        var text = value.Event.Text ?? string.Empty;
        var utf8 = Encoding.UTF8.GetBytes(text);
        var contentId = _cursors.EncodeEventContentId(
            thread.Id,
            value.Sequence,
            rollout.Identity,
            Convert.ToHexString(SHA256.HashData(utf8)).ToLowerInvariant());
        return new ConversationEventDto(
            value.Sequence, value.Event.Kind.ToString(), value.Event.Timestamp, null,
            value.Event.TurnId, [], [], "内容较大，点击加载全文",
            contentId, utf8.Length);
    }

    private static string DeriveStatus(IReadOnlyList<ConversationEventDto> events)
    {
        for (var index = events.Count - 1; index >= 0; index--)
        {
            if (events[index].Kind == nameof(ConversationEventKind.TaskCompleted))
            {
                return "idle";
            }

            if (events[index].Kind == nameof(ConversationEventKind.TaskStarted))
            {
                return "running";
            }
        }

        return "idle";
    }
}
