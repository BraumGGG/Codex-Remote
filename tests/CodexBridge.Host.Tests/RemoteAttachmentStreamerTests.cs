using System.Collections.Concurrent;
using System.Text.Json;
using CodexBridge.Host.Remote;
using CodexBridge.Remote.Protocol;

namespace CodexBridge.Host.Tests;

public sealed class RemoteAttachmentStreamerTests
{
    [Fact]
    public async Task StreamImage_TransfersExactlyTwoMiBInHashedChunks()
    {
        var bytes = new byte[2 * 1024 * 1024];
        new Random(42).NextBytes(bytes);
        var source = new FakeSource(bytes, 2 * 1024 * 1024);
        var sent = new List<RemoteFrame>();
        await using var writer = new PriorityFrameWriter((frame, _) =>
        {
            sent.Add(frame);
            return Task.CompletedTask;
        });

        await new RemoteAttachmentStreamer(source).StreamImageAsync(
            "thread", "image", Guid.NewGuid(), writer, CancellationToken.None);

        Assert.Equal(RemoteFrameKind.AttachmentStart, sent.First().Kind);
        Assert.Equal(32, sent.Count(frame => frame.Kind == RemoteFrameKind.AttachmentChunk));
        Assert.Equal(bytes, sent
            .Where(frame => frame.Kind == RemoteFrameKind.AttachmentChunk)
            .SelectMany(frame => frame.Payload.ToArray())
            .ToArray());
        var completed = JsonDocument.Parse(sent.Last().Payload).RootElement;
        Assert.Equal(bytes.Length, completed.GetProperty("length").GetInt64());
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
            completed.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task Stream_RejectsOneByteOverLimitBeforeWritingFrames()
    {
        var source = new FakeSource(new byte[101], 100);
        var count = 0;
        await using var writer = new PriorityFrameWriter((_, _) =>
        {
            count++;
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new RemoteAttachmentStreamer(source).StreamImageAsync(
                "thread", "image", Guid.NewGuid(), writer, CancellationToken.None));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task StreamEventText_TransfersFourMiBWithEventTextMetadata()
    {
        var bytes = new byte[4 * 1024 * 1024];
        Array.Fill(bytes, (byte)'x');
        var source = new FakeSource(bytes, 4 * 1024 * 1024);
        var sent = new List<RemoteFrame>();
        await using var writer = new PriorityFrameWriter((frame, _) =>
        {
            sent.Add(frame);
            return Task.CompletedTask;
        });

        await new RemoteAttachmentStreamer(source).StreamEventTextAsync(
            "thread", 42, "content", Guid.NewGuid(), writer, CancellationToken.None);

        var started = JsonDocument.Parse(sent.First().Payload).RootElement;
        Assert.Equal("event-text", started.GetProperty("kind").GetString());
        Assert.Equal(bytes.Length, started.GetProperty("length").GetInt64());
        Assert.Equal(64, sent.Count(frame => frame.Kind == RemoteFrameKind.AttachmentChunk));
        Assert.Equal(bytes, sent
            .Where(frame => frame.Kind == RemoteFrameKind.AttachmentChunk)
            .SelectMany(frame => frame.Payload.ToArray())
            .ToArray());
    }

    [Fact]
    public async Task PriorityWriter_SendsControlBeforeQueuedAttachment()
    {
        var sent = new ConcurrentQueue<RemoteFrameKind>();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var writer = new PriorityFrameWriter(async (frame, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstEntered.SetResult();
                await release.Task;
            }
            sent.Enqueue(frame.Kind);
        });
        var id = Guid.NewGuid();
        var first = writer.WriteAttachmentAsync(new RemoteFrame(
            RemoteFrameKind.AttachmentChunk, id, 1, new byte[1]));
        await firstEntered.Task;
        var second = writer.WriteAttachmentAsync(new RemoteFrame(
            RemoteFrameKind.AttachmentChunk, id, 2, new byte[1]));
        var control = writer.WriteControlAsync(new RemoteFrame(
            RemoteFrameKind.Event, id, 3, new byte[1]));
        release.SetResult();
        await Task.WhenAll(first, second, control);

        Assert.Equal(
            [RemoteFrameKind.AttachmentChunk, RemoteFrameKind.Event, RemoteFrameKind.AttachmentChunk],
            sent.ToArray());
    }

    private sealed class FakeSource(byte[] bytes, int maximumBytes) : IRemoteAttachmentSource
    {
        public Task<RemoteAttachmentContent> OpenImageAsync(
            string threadId, string attachmentId, CancellationToken cancellationToken) =>
            Open("image", "image/png");
        public Task<RemoteAttachmentContent> OpenTextAsync(
            string threadId, string fileId, CancellationToken cancellationToken) =>
            Open("text", "markdown");
        public Task<RemoteAttachmentContent> OpenEventTextAsync(
            string threadId, long sequence, string contentId, CancellationToken cancellationToken) =>
            Open("event-text", "text/plain; charset=utf-8");
        private Task<RemoteAttachmentContent> Open(string kind, string contentType) =>
            Task.FromResult(new RemoteAttachmentContent(
                kind,
                "file",
                contentType,
                new MemoryStream(bytes, writable: false),
                maximumBytes));
    }
}
