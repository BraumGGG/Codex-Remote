using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using CodexBridge.Core;
using CodexBridge.Host.Contracts;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Win32.SafeHandles;

namespace CodexBridge.Host.Services;

public sealed class TextFilePreviewPolicy
{
    public const int MaxFileBytes = 2 * 1024 * 1024;
}

public sealed record TextFileEventProjection(
    string? Text,
    IReadOnlyList<TextFileAttachmentDto> Files);

public sealed class TextFileAttachmentService
{
    private static readonly MarkdownPipeline ParserPipeline = new MarkdownPipelineBuilder()
        .UsePreciseSourceLocation()
        .Build();

    private static readonly IReadOnlyDictionary<string, string> SupportedExtensions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".md"] = "markdown",
            [".txt"] = "text",
            [".json"] = "text",
            [".yaml"] = "text",
            [".yml"] = "text",
            [".csv"] = "text",
            [".log"] = "text",
        };

    private readonly IThreadCatalog _catalog;
    private readonly IConversationReader _reader;

    public TextFileAttachmentService(
        IThreadCatalog catalog,
        IConversationReader reader)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public TextFileEventProjection ProjectEvent(
        string threadId,
        string projectRoot,
        ConversationEventKind kind,
        string? text)
    {
        if (kind != ConversationEventKind.AgentMessage || string.IsNullOrEmpty(text))
        {
            return new TextFileEventProjection(text, []);
        }

        var parsed = ParseLinks(threadId, projectRoot, text);
        return new TextFileEventProjection(
            ApplyReplacements(text, parsed.Replacements),
            parsed.References
                .Select(reference => new TextFileAttachmentDto(
                    reference.Id,
                    reference.Name,
                    reference.Kind))
                .ToArray());
    }

    public async Task<TextFilePreviewDto> GetAsync(
        string threadId,
        string fileId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileId))
        {
            throw new KeyNotFoundException("Text file does not exist.");
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
            throw new KeyNotFoundException("Text file does not exist.");
        }

        await foreach (var item in _reader.ReadAsync(
                           thread.RolloutPath,
                           follow: false,
                           cancellationToken))
        {
            if (item.Kind != ConversationEventKind.AgentMessage || string.IsNullOrEmpty(item.Text))
            {
                continue;
            }

            var reference = ParseLinks(threadId, thread.Cwd, item.Text).References.FirstOrDefault(
                candidate => string.Equals(candidate.Id, fileId, StringComparison.Ordinal));
            if (reference is not null)
            {
                return await ReadPreviewAsync(reference, cancellationToken);
            }
        }

        throw new KeyNotFoundException("Text file does not exist.");
    }

    private ParsedLinks ParseLinks(string threadId, string projectRoot, string message)
    {
        var document = Markdown.Parse(message, ParserPipeline);
        var references = new Dictionary<string, FileReference>(StringComparer.Ordinal);
        var replacements = new List<TextReplacement>();

        foreach (var link in document.Descendants<LinkInline>().Where(link => !link.IsImage))
        {
            var url = GetOriginalUrl(message, link);
            if (string.IsNullOrWhiteSpace(url) || !Path.IsPathRooted(url))
            {
                continue;
            }

            var displayName = GetSafeFileName(url);
            replacements.Add(new TextReplacement(url, displayName));

            var reference = TryCreateReference(threadId, projectRoot, url);
            if (reference is not null)
            {
                references.TryAdd(reference.Id, reference);
            }
        }

        return new ParsedLinks(references.Values.ToArray(), replacements);
    }

    private static string? GetOriginalUrl(string source, LinkInline link)
    {
        var span = link.UrlSpan;
        if (span.Start >= 0 && span.End >= span.Start && span.End < source.Length)
        {
            var original = source.Substring(span.Start, span.End - span.Start + 1);
            if (original.Length >= 2 && original[0] == '<' && original[^1] == '>')
            {
                return original[1..^1];
            }

            return original;
        }

        return link.Url;
    }

    private FileReference? TryCreateReference(string threadId, string projectRoot, string path)
    {
        string normalizedPath;
        string normalizedProjectRoot;
        try
        {
            normalizedPath = TargetPolicy.NormalizePath(path);
            normalizedProjectRoot = TargetPolicy.NormalizePath(projectRoot);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!IsInsideProject(normalizedPath, normalizedProjectRoot) ||
            !SupportedExtensions.TryGetValue(Path.GetExtension(normalizedPath), out var kind))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(
                normalizedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > TextFilePreviewPolicy.MaxFileBytes ||
                !IsInsideProject(GetFinalPath(stream.SafeFileHandle), normalizedProjectRoot))
            {
                return null;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var hashInput = $"{threadId}\0{normalizedPath.ToUpperInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(hashInput));
        return new FileReference(
            Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant(),
            normalizedPath,
            normalizedProjectRoot,
            Path.GetFileName(normalizedPath),
            kind);
    }

    private async Task<TextFilePreviewDto> ReadPreviewAsync(
        FileReference reference,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                reference.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > TextFilePreviewPolicy.MaxFileBytes ||
                !IsInsideProject(GetFinalPath(stream.SafeFileHandle), reference.ProjectRoot))
            {
                throw new KeyNotFoundException("Text file does not exist.");
            }

            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true);
            var content = await reader.ReadToEndAsync(cancellationToken);
            return new TextFilePreviewDto(reference.Name, reference.Kind, content);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new KeyNotFoundException("Text file does not exist.", exception);
        }
    }

    private static bool IsInsideProject(string path, string projectRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(projectRoot) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetSafeFileName(string path)
    {
        try
        {
            return Path.GetFileName(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "文件";
        }
    }

    private static string ApplyReplacements(
        string source,
        IReadOnlyList<TextReplacement> replacements)
    {
        foreach (var replacement in replacements
                     .OrderByDescending(item => item.Path.Length))
        {
            source = source.Replace(
                $"({replacement.Path})",
                string.Empty,
                StringComparison.OrdinalIgnoreCase);
            source = source.Replace(
                replacement.Path,
                replacement.Text,
                StringComparison.OrdinalIgnoreCase);
        }

        return source;
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0)
        {
            throw new IOException("Unable to resolve the opened file path.");
        }

        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity)
            {
                throw new IOException("Unable to resolve the opened file path.");
            }
        }

        return TargetPolicy.NormalizePath(buffer.ToString());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    private sealed record FileReference(
        string Id,
        string Path,
        string ProjectRoot,
        string Name,
        string Kind);

    private sealed record TextReplacement(string Path, string Text);

    private sealed record ParsedLinks(
        IReadOnlyList<FileReference> References,
        IReadOnlyList<TextReplacement> Replacements);
}
