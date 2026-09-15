using System.Security.Cryptography;
using System.Text;
using CodexBridge.Core;
using CodexBridge.Host.Contracts;
using CodexBridge.Windows;

namespace CodexBridge.Host.Services;

public sealed class ConversationStreamService
{
    private readonly IThreadCatalog _catalog;
    private readonly IConversationReader _reader;
    private readonly TargetPolicy _policy;
    private readonly TextFileAttachmentService? _fileAttachments;
    private readonly IRolloutIndexStore? _index;
    private readonly PagingCursorCodec _cursors;

    public ConversationStreamService(
        IThreadCatalog catalog,
        IConversationReader reader,
        TargetPolicy policy,
        TextFileAttachmentService? fileAttachments = null,
        IRolloutIndexStore? index = null,
        PagingCursorCodec? cursors = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _fileAttachments = fileAttachments;
        _index = index;
        _cursors = cursors ?? new PagingCursorCodec();
    }

    public async IAsyncEnumerable<SseEvent> StreamAsync(
        string threadId,
        long afterSequence,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        if (afterSequence < 0 || string.IsNullOrWhiteSpace(threadId))
        {
            throw new KeyNotFoundException("会话不存在。");
        }

        ThreadSummary? thread;
        try { thread = await _catalog.GetAsync(threadId, cancellationToken); }
        catch (UnauthorizedAccessException) { thread = null; }
        if (thread is null)
        {
            throw new KeyNotFoundException("会话不存在。");
        }

        if (_index is not null && _reader is RolloutConversationReader rolloutReader)
        {
            await foreach (var indexed in StreamIndexedAsync(
                threadId, thread, afterSequence, rolloutReader, cancellationToken))
                yield return indexed;
            yield break;
        }

        var rolloutIdentity = WorkspaceQueryService.GetRolloutSnapshot(thread.RolloutPath).Identity;
        long sequence = 0;
        await foreach (var item in _reader.ReadAsync(
                           thread.RolloutPath,
                           follow: true,
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

            var projection = _fileAttachments?.ProjectEvent(
                threadId,
                thread.Cwd,
                item.Kind,
                item.Text)
                ?? new TextFileEventProjection(item.Text, []);
            yield return Project(threadId, thread, sequence, item, rolloutIdentity);
        }
    }

    private async IAsyncEnumerable<SseEvent> StreamIndexedAsync(
        string threadId,
        ThreadSummary thread,
        long afterSequence,
        RolloutConversationReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var summary = await _index!.GetSummaryAsync(thread.RolloutPath, cancellationToken).ConfigureAwait(false);
        var missingCount = checked((int)Math.Min(int.MaxValue, Math.Max(0, summary.LatestSequence - afterSequence)));
        if (missingCount > 0)
        {
            var missing = await _index.GetPageAsync(
                thread.RolloutPath, summary.LatestSequence + 1, missingCount, cancellationToken).ConfigureAwait(false);
            foreach (var value in missing.Where(value => value.Sequence > afterSequence))
                yield return Project(threadId, thread, value.Sequence, value.Event, summary.Identity);
        }

        var sequence = summary.LatestSequence;
        await foreach (var item in reader.ReadFromOffsetAsync(
            thread.RolloutPath, summary.IndexedLength, follow: true, cancellationToken))
        {
            if (item.Kind == ConversationEventKind.Unknown) continue;
            yield return Project(threadId, thread, ++sequence, item, summary.Identity);
        }
    }

    private SseEvent Project(
        string threadId,
        ThreadSummary thread,
        long sequence,
        ConversationEvent item,
        string rolloutIdentity)
    {
        var projection = _fileAttachments?.ProjectEvent(threadId, thread.Cwd, item.Kind, item.Text)
            ?? new TextFileEventProjection(item.Text, []);
        var text = projection.Text;
        var utf8 = text is null ? [] : Encoding.UTF8.GetBytes(text);
        var isLarge = utf8.Length > WorkspaceQueryService.InlineEventTextBytes;
        var preview = isLarge ? DecodeUtf8Prefix(utf8, WorkspaceQueryService.InlineEventTextBytes) : null;
        var contentId = isLarge
            ? _cursors.EncodeEventContentId(
                threadId,
                sequence,
                rolloutIdentity,
                Convert.ToHexString(SHA256.HashData(utf8)).ToLowerInvariant())
            : null;
        return new SseEvent(
            sequence,
            item.Kind.ToString(),
            item.Timestamp,
            isLarge ? null : text,
            item.TurnId,
            ImageAttachmentService.CreateDtos(item.LocalImagePaths),
            projection.Files,
            preview,
            contentId,
            utf8.Length);
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
}
