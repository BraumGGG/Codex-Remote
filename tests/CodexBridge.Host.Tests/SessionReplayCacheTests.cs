using CodexBridge.Host.Remote;

namespace CodexBridge.Host.Tests;

public sealed class SessionReplayCacheTests
{
    [Fact]
    public void TryConsume_AcceptsFreshSessionOnlyOnce()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 8, 16, 8, 0, 0, TimeSpan.Zero));
        var cache = new SessionReplayCache(clock);
        var sessionId = RemoteEncoding.Base64UrlEncode(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());

        Assert.Equal(SessionConsumeResult.Success, cache.TryConsume(sessionId, clock.GetUtcNow()));
        Assert.Equal(SessionConsumeResult.Replayed, cache.TryConsume(sessionId, clock.GetUtcNow()));
    }

    [Fact]
    public void TryConsume_RejectsExpiredFutureAndMalformedSessions()
    {
        var now = new DateTimeOffset(2026, 8, 16, 8, 0, 0, TimeSpan.Zero);
        var clock = new TestTimeProvider(now);
        var cache = new SessionReplayCache(clock);
        var sessionId = RemoteEncoding.Base64UrlEncode(new byte[16]);

        Assert.Equal(SessionConsumeResult.Expired, cache.TryConsume(
            sessionId,
            now - TimeSpan.FromMinutes(2) - TimeSpan.FromMilliseconds(1)));
        Assert.Equal(SessionConsumeResult.Future, cache.TryConsume(
            sessionId,
            now + TimeSpan.FromSeconds(31)));
        Assert.Equal(SessionConsumeResult.Invalid, cache.TryConsume("short", now));
    }

    [Fact]
    public void TryConsume_PrunesEntriesAfterTenMinutes()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 8, 16, 8, 0, 0, TimeSpan.Zero));
        var cache = new SessionReplayCache(clock);
        var sessionId = RemoteEncoding.Base64UrlEncode(new byte[16]);
        Assert.Equal(SessionConsumeResult.Success, cache.TryConsume(sessionId, clock.GetUtcNow()));

        clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromMilliseconds(1));

        Assert.Equal(SessionConsumeResult.Success, cache.TryConsume(sessionId, clock.GetUtcNow()));
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}
