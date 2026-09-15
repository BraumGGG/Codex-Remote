using System.Text;
using System.Text.Json;
using CodexBridge.Remote.Protocol;

namespace CodexBridge.Remote.Protocol.Tests;

public sealed class RemoteFrameCodecTests
{
    [Fact]
    public void Encode_MatchesCrossLanguageVector()
    {
        var frame = new RemoteFrame(
            RemoteFrameKind.Request,
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            1,
            "abc"u8.ToArray());

        Assert.Equal(
            "0101000000112233445566778899AABBCCDDEEFF000000000000000100000003616263",
            Convert.ToHexString(RemoteFrameCodec.Encode(frame)));
    }

    [Fact]
    public void RoundTrip_PreservesFrame()
    {
        var original = new RemoteFrame(RemoteFrameKind.Event, Guid.NewGuid(), 42, "payload"u8.ToArray());
        var decoded = RemoteFrameCodec.Decode(RemoteFrameCodec.Encode(original));
        Assert.Equal(original.Kind, decoded.Kind);
        Assert.Equal(original.RequestId, decoded.RequestId);
        Assert.Equal(original.Sequence, decoded.Sequence);
        Assert.Equal(original.Payload.ToArray(), decoded.Payload.ToArray());
    }

    [Theory]
    [InlineData(0, "unsupported_version")]
    [InlineData(1, "unknown_frame_kind")]
    [InlineData(2, "unsupported_flags")]
    [InlineData(3, "invalid_sequence")]
    [InlineData(4, "length_mismatch")]
    [InlineData(5, "payload_too_large")]
    public void Decode_RejectsInvalidFrame(int mutation, string errorCode)
    {
        var bytes = RemoteFrameCodec.Encode(new RemoteFrame(
            RemoteFrameKind.Request,
            Guid.NewGuid(),
            0,
            Array.Empty<byte>()));
        switch (mutation)
        {
            case 0: bytes[0] = 2; break;
            case 1: bytes[1] = 255; break;
            case 2: bytes[3] = 1; break;
            case 3: Array.Fill(bytes, (byte)0xff, 20, 8); break;
            case 4: bytes[31] = 1; break;
            case 5:
                BitConverter.TryWriteBytes(bytes.AsSpan(28, 4),
                    System.Net.IPAddress.HostToNetworkOrder(RemoteFrameCodec.MaximumPayloadLength + 1));
                break;
        }
        var error = Assert.Throws<RemoteProtocolException>(() => RemoteFrameCodec.Decode(bytes));
        Assert.Equal(errorCode, error.ErrorCode);
    }

    [Fact]
    public void RpcRequest_RejectsUnknownAndDangerousMethodNames()
    {
        var unknown = Encoding.UTF8.GetBytes("{\"method\":255,\"parameters\":{}}");
        Assert.Equal("unknown_rpc_method", Assert.Throws<RemoteProtocolException>(
            () => RpcJson.DecodeRequest(unknown)).ErrorCode);

        foreach (var method in new[] { "Delete", "Archive", "Rename", "Pin", "Shell", "GenericRpc" })
        {
            var payload = Encoding.UTF8.GetBytes($"{{\"method\":\"{method}\",\"parameters\":{{}}}}");
            Assert.Equal("invalid_rpc", Assert.Throws<RemoteProtocolException>(
                () => RpcJson.DecodeRequest(payload)).ErrorCode);
        }
    }

    [Fact]
    public void Parameters_RejectPathsUrlsAndUnknownFields()
    {
        using var document = JsonDocument.Parse(
            "{\"threadId\":\"allowed\",\"path\":\"C:\\\\secret\",\"url\":\"https://example.test\"}");
        Assert.Equal("invalid_parameters", Assert.Throws<RemoteProtocolException>(
            () => RpcJson.DecodeParameters<ThreadParameters>(document.RootElement)).ErrorCode);
    }
}
