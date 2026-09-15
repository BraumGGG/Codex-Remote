using CodexBridge.Host.Remote;

namespace CodexBridge.Host.Tests;

public sealed class RemotePairingServiceTests
{
    [Fact]
    public void Issue_Uses128BitSecretAndConsumesOnlyOnce()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 8, 16, 8, 0, 0, TimeSpan.Zero));
        var service = new RemotePairingService(clock);
        var pairing = service.Issue();
        var payload = "first remote offer"u8.ToArray();
        var proof = RemotePairingService.CreateProof(pairing.Secret, payload);

        Assert.Equal(16, RemoteEncoding.Base64UrlDecode(pairing.Secret).Length);
        Assert.Equal(RemotePairingConsumeResult.Success, service.Consume(
            pairing.RouteId,
            payload,
            proof));
        Assert.Equal(RemotePairingConsumeResult.Used, service.Consume(
            pairing.RouteId,
            payload,
            proof));
    }

    [Fact]
    public void Consume_RejectsWrongProofWithoutBurningPairing()
    {
        var service = new RemotePairingService(TimeProvider.System);
        var pairing = service.Issue();
        var payload = "offer"u8.ToArray();

        Assert.Equal(RemotePairingConsumeResult.Invalid, service.Consume(
            pairing.RouteId,
            payload,
            RemotePairingService.CreateProof(pairing.Secret, "different"u8.ToArray())));
        Assert.Equal(RemotePairingConsumeResult.Success, service.Consume(
            pairing.RouteId,
            payload,
            RemotePairingService.CreateProof(pairing.Secret, payload)));
    }

    [Fact]
    public void Consume_ExpiresAtFiveMinutes()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 8, 16, 8, 0, 0, TimeSpan.Zero));
        var service = new RemotePairingService(clock);
        var pairing = service.Issue();
        var payload = "offer"u8.ToArray();
        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(RemotePairingConsumeResult.Expired, service.Consume(
            pairing.RouteId,
            payload,
            RemotePairingService.CreateProof(pairing.Secret, payload)));
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}
