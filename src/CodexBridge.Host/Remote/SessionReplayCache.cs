namespace CodexBridge.Host.Remote;

public enum SessionConsumeResult
{
    Success,
    Invalid,
    Expired,
    Future,
    Replayed,
}

public sealed class SessionReplayCache
{
    private static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);
    private const int MaximumEntries = 4096;

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, DateTimeOffset> _consumed = new(StringComparer.Ordinal);

    public SessionReplayCache(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public SessionConsumeResult TryConsume(string sessionId, DateTimeOffset issuedAt)
    {
        byte[] sessionBytes;
        try
        {
            sessionBytes = RemoteEncoding.Base64UrlDecode(sessionId);
        }
        catch (Exception exception) when (exception is ArgumentNullException or FormatException)
        {
            return SessionConsumeResult.Invalid;
        }

        if (sessionBytes.Length != 16)
        {
            return SessionConsumeResult.Invalid;
        }

        var now = _timeProvider.GetUtcNow();
        if (issuedAt < now - MaximumAge)
        {
            return SessionConsumeResult.Expired;
        }

        if (issuedAt > now + MaximumClockSkew)
        {
            return SessionConsumeResult.Future;
        }

        lock (_gate)
        {
            foreach (var expired in _consumed
                         .Where(item => now > item.Value + Retention)
                         .Select(item => item.Key)
                         .ToArray())
            {
                _consumed.Remove(expired);
            }

            if (_consumed.ContainsKey(sessionId))
            {
                return SessionConsumeResult.Replayed;
            }

            if (_consumed.Count >= MaximumEntries)
            {
                var oldest = _consumed.MinBy(item => item.Value).Key;
                _consumed.Remove(oldest);
            }

            _consumed.Add(sessionId, now);
            return SessionConsumeResult.Success;
        }
    }
}
