namespace CodexBridge.Remote.Protocol;

public enum RemoteFrameKind : byte
{
    Request = 1,
    Response = 2,
    Event = 3,
    AttachmentStart = 4,
    AttachmentChunk = 5,
    AttachmentComplete = 6,
    AttachmentCancel = 7,
    Error = 8,
}

public sealed record RemoteFrame(
    RemoteFrameKind Kind,
    Guid RequestId,
    long Sequence,
    ReadOnlyMemory<byte> Payload,
    ushort Flags = 0);

public sealed class RemoteProtocolException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}
