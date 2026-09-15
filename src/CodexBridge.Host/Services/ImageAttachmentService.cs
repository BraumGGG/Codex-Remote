using System.Security.Cryptography;
using System.Text;
using CodexBridge.Core;
using CodexBridge.Host.Contracts;

namespace CodexBridge.Host.Services;

public sealed record ImageAttachment(string Path, string ContentType);

public sealed class ImageAttachmentService
{
    private readonly IThreadCatalog _catalog;
    private readonly IConversationReader _reader;

    public ImageAttachmentService(
        IThreadCatalog catalog,
        IConversationReader reader)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public static IReadOnlyList<ImageAttachmentDto> CreateDtos(
        IReadOnlyList<string>? localImagePaths) =>
        localImagePaths is null
            ? []
            : localImagePaths
                .Select(TryCreateReference)
                .Where(reference => reference is not null)
                .Select(reference => new ImageAttachmentDto(reference!.Value.Id))
                .ToArray();

    public async Task<ImageAttachment> GetAsync(
        string threadId,
        string attachmentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(attachmentId))
        {
            throw new KeyNotFoundException("Image attachment does not exist.");
        }

        ThreadSummary? thread;
        try
        {
            thread = await _catalog.GetAsync(threadId, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            thread = null;
        }
        if (thread is null)
        {
            throw new KeyNotFoundException("Image attachment does not exist.");
        }

        await foreach (var item in _reader.ReadAsync(
                           thread.RolloutPath,
                           follow: false,
                           cancellationToken))
        {
            foreach (var path in item.LocalImagePaths ?? [])
            {
                var reference = TryCreateReference(path);
                if (reference is null ||
                    !string.Equals(reference.Value.Id, attachmentId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!File.Exists(reference.Value.Path) ||
                    !await HasExpectedSignatureAsync(
                        reference.Value.Path,
                        reference.Value.ContentType,
                        cancellationToken))
                {
                    throw new KeyNotFoundException("Image attachment does not exist.");
                }

                return new ImageAttachment(reference.Value.Path, reference.Value.ContentType);
            }
        }

        throw new KeyNotFoundException("Image attachment does not exist.");
    }

    private static (string Id, string Path, string ContentType)? TryCreateReference(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            return null;
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var contentType = Path.GetExtension(normalizedPath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => null,
        };
        if (contentType is null)
        {
            return null;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath.ToUpperInvariant()));
        return (Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant(), normalizedPath, contentType);
    }

    private static async Task<bool> HasExpectedSignatureAsync(
        string path,
        string contentType,
        CancellationToken cancellationToken)
    {
        var header = new byte[12];
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: header.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var count = await stream.ReadAsync(header, cancellationToken);
            return contentType switch
            {
                "image/jpeg" => count >= 3 && header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff,
                "image/png" => count >= 8 && header.AsSpan(0, 8).SequenceEqual(
                    new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
                "image/gif" => count >= 6 &&
                    (header.AsSpan(0, 6).SequenceEqual("GIF87a"u8) ||
                     header.AsSpan(0, 6).SequenceEqual("GIF89a"u8)),
                "image/webp" => count >= 12 &&
                    header.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
                    header.AsSpan(8, 4).SequenceEqual("WEBP"u8),
                _ => false,
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
