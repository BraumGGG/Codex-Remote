using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexBridge.Remote.Protocol;

public sealed record RpcRequest(RpcMethod Method, JsonElement Parameters);
public sealed record RpcResponse(bool Success, JsonElement? Result, string? ErrorCode);

public sealed record EmptyParameters;
public sealed record ProjectParameters(string ProjectId);
public sealed record ThreadsParameters(string ProjectId, string? Cursor = null, int PageSize = 30, string? Query = null);
public sealed record ThreadParameters(string ThreadId);
public sealed record EventsParameters(
    string ThreadId,
    string? BeforeCursor = null,
    int PageSize = 40,
    int MaximumBytes = 48 * 1024);
public sealed record AttachmentParameters(string ThreadId, string AttachmentId);
public sealed record TextFileParameters(string ThreadId, string FileId);
public sealed record EventTextParameters(string ThreadId, long Sequence, string ContentId);
public sealed record SubscribeParameters(string ThreadId, long AfterSequence);
public sealed record UnsubscribeParameters(string ThreadId);
public sealed record SubmitTextParameters(Guid CommandId, string ThreadId, string Text);
public sealed record CancelTransferParameters(Guid TransferId);

public static class RpcJson
{
    public const int MaximumTextLength = 32 * 1024;
    public const int MaximumResponsePayloadLength = 48 * 1024;
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static RpcRequest DecodeRequest(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > RemoteFrameCodec.MaximumPayloadLength)
            throw new RemoteProtocolException("payload_too_large", "RPC payload exceeds its limit.");
        try
        {
            var request = JsonSerializer.Deserialize<RpcRequest>(payload, Options)
                ?? throw new RemoteProtocolException("invalid_rpc", "RPC request is empty.");
            if (!Enum.IsDefined(request.Method))
                throw new RemoteProtocolException("unknown_rpc_method", "RPC method is not allowed.");
            return request;
        }
        catch (JsonException exception)
        {
            throw new RemoteProtocolException("invalid_rpc", $"RPC request is invalid: {exception.Message}");
        }
    }

    public static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T DecodeParameters<T>(JsonElement parameters) where T : class
    {
        try
        {
            return parameters.Deserialize<T>(Options)
                ?? throw new RemoteProtocolException("invalid_parameters", "RPC parameters are empty.");
        }
        catch (JsonException exception)
        {
            throw new RemoteProtocolException("invalid_parameters", $"RPC parameters are invalid: {exception.Message}");
        }
    }
}
