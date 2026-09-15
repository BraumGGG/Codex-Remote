using System.Buffers.Binary;

namespace CodexBridge.Host.Remote;

public enum TransportPipeMessageKind : byte
{
    Offer = 1,
    HostData = 2,
    Close = 3,
    Answer = 11,
    RemoteData = 12,
    ChannelOpen = 13,
    ChannelClosed = 14,
    Error = 15,
    Diagnostic = 16,
}

public sealed record TransportPipeMessage(
    TransportPipeMessageKind Kind,
    ReadOnlyMemory<byte> Payload);

public static class TransportPipeCodec
{
    public const int HeaderLength = 5;
    public const int MaximumPayloadLength = 128 * 1024;

    public static async Task WriteAsync(
        Stream stream,
        TransportPipeMessage message,
        CancellationToken cancellationToken)
    {
        Validate(message.Kind, message.Payload.Length);
        var header = new byte[HeaderLength];
        header[0] = (byte)message.Kind;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), message.Payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (!message.Payload.IsEmpty)
            await stream.WriteAsync(message.Payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<TransportPipeMessage?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[HeaderLength];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken).ConfigureAwait(false);
        var kind = (TransportPipeMessageKind)header[0];
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        Validate(kind, length);
        var payload = GC.AllocateUninitializedArray<byte>(length);
        if (length > 0) await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return new TransportPipeMessage(kind, payload);
    }

    private static void Validate(TransportPipeMessageKind kind, int length)
    {
        if (!Enum.IsDefined(kind)) throw new InvalidDataException("Unknown transport pipe message kind.");
        if (length is < 0 or > MaximumPayloadLength)
            throw new InvalidDataException("Transport pipe message exceeds its limit.");
    }
}
