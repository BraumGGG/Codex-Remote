using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexBridge.Remote.Protocol;

namespace CodexBridge.Host.Services;

public sealed class PagingCursorCodec
{
    private readonly byte[] _key;

    public PagingCursorCodec() : this(RandomNumberGenerator.GetBytes(32)) { }

    internal PagingCursorCodec(byte[] key)
    {
        _key = key.Length >= 32 ? key.ToArray() : throw new ArgumentException("Cursor key is too short.", nameof(key));
    }

    public string EncodeThreadCursor(string projectId, string? query, long updatedAtMs, string threadId)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new ThreadCursor(
            1, projectId, HashQuery(query), updatedAtMs, threadId), RpcJson.Options);
        var signature = HMACSHA256.HashData(_key, payload);
        return $"{Encode(payload)}.{Encode(signature)}";
    }

    public (long UpdatedAtMs, string ThreadId) DecodeThreadCursor(
        string cursor,
        string projectId,
        string? query)
    {
        try
        {
            var parts = cursor.Split('.', 2);
            if (parts.Length != 2) throw new FormatException();
            var payload = Decode(parts[0]);
            var signature = Decode(parts[1]);
            var expected = HMACSHA256.HashData(_key, payload);
            if (!CryptographicOperations.FixedTimeEquals(signature, expected)) throw new FormatException();
            var value = JsonSerializer.Deserialize<ThreadCursor>(payload, RpcJson.Options)
                ?? throw new FormatException();
            if (value.Version != 1 ||
                !string.Equals(value.ProjectId, projectId, StringComparison.Ordinal) ||
                !string.Equals(value.QueryHash, HashQuery(query), StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(value.ThreadId))
                throw new FormatException();
            return (value.UpdatedAtMs, value.ThreadId);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new RemoteProtocolException("invalid_cursor", "Paging cursor is invalid.");
        }
    }

    public string EncodeEventCursor(
        string threadId,
        string rolloutIdentity,
        long sourceLength,
        long beforeSequence)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new EventCursor(1, threadId, rolloutIdentity, sourceLength, beforeSequence), RpcJson.Options);
        var signature = HMACSHA256.HashData(_key, payload);
        return $"{Encode(payload)}.{Encode(signature)}";
    }

    public (string RolloutIdentity, long SourceLength, long BeforeSequence) DecodeEventCursor(
        string cursor,
        string threadId)
    {
        try
        {
            var parts = cursor.Split('.', 2);
            if (parts.Length != 2) throw new FormatException();
            var payload = Decode(parts[0]);
            var signature = Decode(parts[1]);
            var expected = HMACSHA256.HashData(_key, payload);
            if (!CryptographicOperations.FixedTimeEquals(signature, expected)) throw new FormatException();
            var value = JsonSerializer.Deserialize<EventCursor>(payload, RpcJson.Options)
                ?? throw new FormatException();
            if (value.Version != 1 ||
                !string.Equals(value.ThreadId, threadId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(value.RolloutIdentity) ||
                value.SourceLength < 0 ||
                value.BeforeSequence < 1)
                throw new FormatException();
            return (value.RolloutIdentity, value.SourceLength, value.BeforeSequence);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new RemoteProtocolException("invalid_cursor", "Paging cursor is invalid.");
        }
    }

    public string EncodeEventContentId(
        string threadId,
        long sequence,
        string rolloutIdentity,
        string textHash)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new EventContent(1, threadId, sequence, rolloutIdentity, textHash), RpcJson.Options);
        var signature = HMACSHA256.HashData(_key, payload);
        return $"{Encode(payload)}.{Encode(signature)}";
    }

    public (string RolloutIdentity, string TextHash) DecodeEventContentId(
        string contentId,
        string threadId,
        long sequence)
    {
        try
        {
            var parts = contentId.Split('.', 2);
            if (parts.Length != 2) throw new FormatException();
            var payload = Decode(parts[0]);
            var signature = Decode(parts[1]);
            var expected = HMACSHA256.HashData(_key, payload);
            if (!CryptographicOperations.FixedTimeEquals(signature, expected)) throw new FormatException();
            var value = JsonSerializer.Deserialize<EventContent>(payload, RpcJson.Options)
                ?? throw new FormatException();
            if (value.Version != 1 ||
                !string.Equals(value.ThreadId, threadId, StringComparison.Ordinal) ||
                value.Sequence != sequence ||
                string.IsNullOrWhiteSpace(value.RolloutIdentity) ||
                value.TextHash.Length != 64)
                throw new FormatException();
            return (value.RolloutIdentity, value.TextHash);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new RemoteProtocolException("invalid_content_id", "Event content ID is invalid.");
        }
    }

    private static string HashQuery(string? query) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeQuery(query))).AsSpan(0, 16));

    public static string NormalizeQuery(string? query) => query?.Trim() ?? string.Empty;

    private static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Decode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => throw new FormatException() };
        return Convert.FromBase64String(padded);
    }

    private sealed record ThreadCursor(
        int Version,
        string ProjectId,
        string QueryHash,
        long UpdatedAtMs,
        string ThreadId);

    private sealed record EventCursor(
        int Version,
        string ThreadId,
        string RolloutIdentity,
        long SourceLength,
        long BeforeSequence);

    private sealed record EventContent(
        int Version,
        string ThreadId,
        long Sequence,
        string RolloutIdentity,
        string TextHash);
}
