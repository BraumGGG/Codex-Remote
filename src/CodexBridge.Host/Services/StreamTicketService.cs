using System.Security.Cryptography;
using System.Text;

namespace CodexBridge.Host.Services;

public sealed record IssuedStreamTicket(string Token, DateTimeOffset ExpiresAt);

public sealed class StreamTicketService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, TicketState> _tickets = new(StringComparer.Ordinal);

    public StreamTicketService(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public IssuedStreamTicket Issue(string deviceId, string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        var token = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var expiresAt = _timeProvider.GetUtcNow() + Lifetime;
        lock (_gate)
        {
            RemoveExpired();
            _tickets[Hash(token)] = new TicketState(deviceId, threadId, expiresAt);
        }

        return new IssuedStreamTicket(token, expiresAt);
    }

    public bool Consume(string token, string threadId)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(threadId))
        {
            return false;
        }

        lock (_gate)
        {
            RemoveExpired();
            var key = Hash(token);
            if (!_tickets.Remove(key, out var state))
            {
                return false;
            }

            return string.Equals(state.ThreadId, threadId, StringComparison.Ordinal) &&
                   _timeProvider.GetUtcNow() < state.ExpiresAt;
        }
    }

    private void RemoveExpired()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var key in _tickets
                     .Where(pair => now >= pair.Value.ExpiresAt)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _tickets.Remove(key);
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record TicketState(
        string DeviceId,
        string ThreadId,
        DateTimeOffset ExpiresAt);
}
