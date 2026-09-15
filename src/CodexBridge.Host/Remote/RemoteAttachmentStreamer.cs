using System.Security.Cryptography;
using System.Text;
using CodexBridge.Host.Services;
using CodexBridge.Remote.Protocol;

namespace CodexBridge.Host.Remote;

public sealed record RemoteAttachmentContent(
    string Kind,
    string Name,
    string ContentType,
    Stream Stream,
    int MaximumBytes) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

public interface IRemoteAttachmentSource
{
    Task<RemoteAttachmentContent> OpenImageAsync(
        string threadId,
        string attachmentId,
        CancellationToken cancellationToken);
    Task<RemoteAttachmentContent> OpenTextAsync(
        string threadId,
        string fileId,
        CancellationToken cancellationToken);
    Task<RemoteAttachmentContent> OpenEventTextAsync(
        string threadId,
        long sequence,
        string contentId,
        CancellationToken cancellationToken);
}

public sealed class HostRemoteAttachmentSource(
    ImageAttachmentService images,
    TextFileAttachmentService files,
    WorkspaceQueryService workspace) : IRemoteAttachmentSource
{
    public const int MaximumImageBytes = 2 * 1024 * 1024;
    public const int MaximumTextBytes = 32 * 1024;

    public async Task<RemoteAttachmentContent> OpenImageAsync(
        string threadId,
        string attachmentId,
        CancellationToken cancellationToken)
    {
        var image = await images.GetAsync(threadId, attachmentId, cancellationToken).ConfigureAwait(false);
        var stream = new FileStream(
            image.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new RemoteAttachmentContent("image", "image", image.ContentType, stream, MaximumImageBytes);
    }

    public async Task<RemoteAttachmentContent> OpenTextAsync(
        string threadId,
        string fileId,
        CancellationToken cancellationToken)
    {
        var file = await files.GetAsync(threadId, fileId, cancellationToken).ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetBytes(file.Content);
        return new RemoteAttachmentContent(
            "text",
            file.Name,
            file.Kind,
            new MemoryStream(bytes, writable: false),
            MaximumTextBytes);
    }

    public async Task<RemoteAttachmentContent> OpenEventTextAsync(
        string threadId,
        long sequence,
        string contentId,
        CancellationToken cancellationToken)
    {
        var content = await workspace.GetEventTextAsync(
            threadId, sequence, contentId, cancellationToken).ConfigureAwait(false);
        return new RemoteAttachmentContent(
            "event-text",
            "event-text",
            "text/plain; charset=utf-8",
            new MemoryStream(Encoding.UTF8.GetBytes(content.Content), writable: false),
            WorkspaceQueryService.MaximumEventTextBytes);
    }
}

public sealed class RemoteAttachmentStreamer(IRemoteAttachmentSource source)
{
    public const int ChunkSize = 64 * 1024;

    public Task StreamImageAsync(
        string threadId,
        string attachmentId,
        Guid transferId,
        PriorityFrameWriter writer,
        CancellationToken cancellationToken) =>
        StreamAsync(
            () => source.OpenImageAsync(threadId, attachmentId, cancellationToken),
            transferId,
            writer,
            cancellationToken);

    public Task StreamTextAsync(
        string threadId,
        string fileId,
        Guid transferId,
        PriorityFrameWriter writer,
        CancellationToken cancellationToken) =>
        StreamAsync(
            () => source.OpenTextAsync(threadId, fileId, cancellationToken),
            transferId,
            writer,
            cancellationToken);

    public Task StreamEventTextAsync(
        string threadId,
        long sequence,
        string contentId,
        Guid transferId,
        PriorityFrameWriter writer,
        CancellationToken cancellationToken) =>
        StreamAsync(
            () => source.OpenEventTextAsync(threadId, sequence, contentId, cancellationToken),
            transferId,
            writer,
            cancellationToken);

    private static async Task StreamAsync(
        Func<Task<RemoteAttachmentContent>> openAsync,
        Guid transferId,
        PriorityFrameWriter writer,
        CancellationToken cancellationToken)
    {
        if (transferId == Guid.Empty) throw new ArgumentException("Transfer ID is required.", nameof(transferId));
        await using var content = await openAsync().ConfigureAwait(false);
        if (!content.Stream.CanRead || !content.Stream.CanSeek)
            throw new InvalidDataException("Attachment stream must be readable and seekable.");
        var initialLength = content.Stream.Length;
        if (initialLength < 0 || initialLength > content.MaximumBytes)
            throw new InvalidDataException("Attachment exceeds its size limit.");

        await writer.WriteAttachmentAsync(
            new RemoteFrame(
                RemoteFrameKind.AttachmentStart,
                transferId,
                0,
                RpcJson.Encode(new
                {
                    content.Kind,
                    content.Name,
                    content.ContentType,
                    length = initialLength,
                })),
            cancellationToken).ConfigureAwait(false);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ChunkSize];
        long total = 0;
        long sequence = 0;
        while (total < initialLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(buffer.Length, initialLength - total);
            var count = await content.Stream.ReadAsync(
                buffer.AsMemory(0, requested),
                cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new InvalidDataException("Attachment changed while being read.");
            total += count;
            hash.AppendData(buffer, 0, count);
            await writer.WriteAttachmentAsync(
                new RemoteFrame(
                    RemoteFrameKind.AttachmentChunk,
                    transferId,
                    ++sequence,
                    buffer.AsMemory(0, count).ToArray()),
                cancellationToken).ConfigureAwait(false);
        }
        if (content.Stream.Length != initialLength)
            throw new InvalidDataException("Attachment changed while being read.");

        await writer.WriteAttachmentAsync(
            new RemoteFrame(
                RemoteFrameKind.AttachmentComplete,
                transferId,
                sequence + 1,
                RpcJson.Encode(new
                {
                    length = total,
                    sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                })),
            cancellationToken).ConfigureAwait(false);
    }
}
