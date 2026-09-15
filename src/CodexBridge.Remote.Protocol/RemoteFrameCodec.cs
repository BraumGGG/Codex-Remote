using System.Buffers.Binary;

namespace CodexBridge.Remote.Protocol;

public static class RemoteFrameCodec
{
    public const byte Version = 1;
    public const int HeaderLength = 32;
    public const int MaximumPayloadLength = 64 * 1024;

    public static byte[] Encode(RemoteFrame frame)
    {
        Validate(frame.Kind, frame.RequestId, frame.Sequence, frame.Flags, frame.Payload.Length);
        var encoded = GC.AllocateUninitializedArray<byte>(HeaderLength + frame.Payload.Length);
        var header = encoded.AsSpan(0, HeaderLength);
        header[0] = Version;
        header[1] = (byte)frame.Kind;
        BinaryPrimitives.WriteUInt16BigEndian(header[2..4], frame.Flags);
        if (!frame.RequestId.TryWriteBytes(header[4..20], bigEndian: true, out var bytesWritten) || bytesWritten != 16)
            throw new InvalidOperationException("Request ID could not be encoded.");
        BinaryPrimitives.WriteInt64BigEndian(header[20..28], frame.Sequence);
        BinaryPrimitives.WriteInt32BigEndian(header[28..32], frame.Payload.Length);
        frame.Payload.Span.CopyTo(encoded.AsSpan(HeaderLength));
        return encoded;
    }

    public static RemoteFrame Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length < HeaderLength)
            throw new RemoteProtocolException("frame_too_short", "Remote frame is shorter than its header.");
        if (encoded[0] != Version)
            throw new RemoteProtocolException("unsupported_version", "Remote frame version is unsupported.");

        var kind = (RemoteFrameKind)encoded[1];
        var flags = BinaryPrimitives.ReadUInt16BigEndian(encoded[2..4]);
        var requestId = new Guid(encoded[4..20], bigEndian: true);
        var sequence = BinaryPrimitives.ReadInt64BigEndian(encoded[20..28]);
        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(encoded[28..32]);
        Validate(kind, requestId, sequence, flags, payloadLength);
        if (encoded.Length != HeaderLength + payloadLength)
            throw new RemoteProtocolException("length_mismatch", "Remote frame length does not match its header.");

        return new RemoteFrame(kind, requestId, sequence, encoded[HeaderLength..].ToArray(), flags);
    }

    private static void Validate(
        RemoteFrameKind kind,
        Guid requestId,
        long sequence,
        ushort flags,
        int payloadLength)
    {
        if (!Enum.IsDefined(kind))
            throw new RemoteProtocolException("unknown_frame_kind", "Remote frame kind is unknown.");
        if (requestId == Guid.Empty)
            throw new RemoteProtocolException("invalid_request_id", "Remote request ID cannot be empty.");
        if (sequence < 0)
            throw new RemoteProtocolException("invalid_sequence", "Remote sequence cannot be negative.");
        if (flags != 0)
            throw new RemoteProtocolException("unsupported_flags", "Remote frame flags are unsupported.");
        if (payloadLength is < 0 or > MaximumPayloadLength)
            throw new RemoteProtocolException("payload_too_large", "Remote frame payload exceeds its limit.");
    }
}
