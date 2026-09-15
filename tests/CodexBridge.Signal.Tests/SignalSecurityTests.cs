using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexBridge.Signal;

namespace CodexBridge.Signal.Tests;

public sealed class SignalSecurityTests
{
    [Fact]
    public void RoutingTicket_VerifiesHostSignatureAndRejectsTampering()
    {
        var now = new DateTimeOffset(2026, 8, 16, 10, 0, 0, TimeSpan.Zero);
        var clock = new TestTimeProvider(now);
        using var host = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var device = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var hostPublic = host.ExportSubjectPublicKeyInfo();
        var payload = new RoutingTicketPayload(
            1,
            SignalEncoding.Encode(SHA256.HashData(hostPublic)),
            "device_1",
            SignalEncoding.Encode(device.ExportSubjectPublicKeyInfo()),
            now.AddDays(7).ToUnixTimeSeconds(),
            SignalEncoding.Encode(new byte[16]));
        var ticket = RoutingTicketVerifier.Sign(payload, host);
        var verifier = new RoutingTicketVerifier(clock);

        Assert.Equal("device_1", verifier.Verify(ticket).DeviceId);
        var bytes = SignalEncoding.Decode(ticket.PayloadBase64Url);
        bytes[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => verifier.Verify(ticket with
        {
            PayloadBase64Url = SignalEncoding.Encode(bytes),
        }));
    }

    [Fact]
    public void RoutingTicket_RejectsExpiredAndExcessiveLifetime()
    {
        var now = new DateTimeOffset(2026, 8, 16, 10, 0, 0, TimeSpan.Zero);
        using var host = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var device = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var hostPublic = host.ExportSubjectPublicKeyInfo();
        SignedRoutingTicket Create(DateTimeOffset expiry) => RoutingTicketVerifier.Sign(
            new RoutingTicketPayload(
                1,
                SignalEncoding.Encode(SHA256.HashData(hostPublic)),
                "device",
                SignalEncoding.Encode(device.ExportSubjectPublicKeyInfo()),
                expiry.ToUnixTimeSeconds(),
                SignalEncoding.Encode(new byte[16])),
            host);
        var verifier = new RoutingTicketVerifier(new TestTimeProvider(now));
        Assert.Throws<InvalidDataException>(() => verifier.Verify(Create(now)));
        Assert.Throws<InvalidDataException>(() => verifier.Verify(Create(now.AddDays(32))));
    }

    [Fact]
    public void TurnCredentials_UseTenMinuteCoturnRestHmac()
    {
        var now = new DateTimeOffset(2026, 8, 16, 10, 0, 0, TimeSpan.Zero);
        var secret = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var credential = new TurnCredentialService(secret, new TestTimeProvider(now)).Issue("device_1");
        Assert.Equal(now.AddMinutes(10), credential.ExpiresAt);
        Assert.Equal(
            Convert.ToBase64String(HMACSHA1.HashData(secret, Encoding.UTF8.GetBytes(credential.Username))),
            credential.Credential);
    }

    [Fact]
    public void TurnCredentials_AreUniqueForOverlappingConnectionGenerations()
    {
        var now = new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);
        var secret = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var service = new TurnCredentialService(secret, new TestTimeProvider(now));

        var first = service.Issue("device_1");
        var second = service.Issue("device_1");

        Assert.NotEqual(first.Username, second.Username);
        Assert.StartsWith($"{now.AddMinutes(10).ToUnixTimeSeconds()}:device_1:", first.Username, StringComparison.Ordinal);
        Assert.Equal(
            Convert.ToBase64String(HMACSHA1.HashData(secret, Encoding.UTF8.GetBytes(second.Username))),
            second.Credential);
    }

    [Fact]
    public void Registry_LimitsIpAndExpiresPairingRoutesWithoutQueue()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 8, 16, 10, 0, 0, TimeSpan.Zero));
        var registry = new SignalConnectionRegistry(clock, maximumConnectionsPerIp: 2);
        Assert.True(registry.TryAddHost("host", new SignalConnection("h", "1.2.3.4")));
        Assert.True(registry.TryAddClient("host", "one", new SignalConnection("c1", "1.2.3.4")));
        Assert.False(registry.TryAddClient("host", "two", new SignalConnection("c2", "1.2.3.4")));
        Assert.True(registry.TryAddPairingRoute("route", "host", clock.GetUtcNow().AddMinutes(5)));
        Assert.Equal("host", registry.ResolvePairingRoute("route"));
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(registry.ResolvePairingRoute("route"));
        Assert.Null(registry.GetClient("host", "offline"));
        Assert.Equal(new SignalRegistrySnapshot(1, 1, 0), registry.Snapshot());
    }

    [Fact]
    public void Registry_NewPairingRouteAtomicallyReplacesPreviousRouteForHost()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 8, 17, 10, 0, 0, TimeSpan.Zero));
        var registry = new SignalConnectionRegistry(clock);
        Assert.True(registry.TryAddHost("host", new SignalConnection("h", "1.2.3.4")));

        Assert.True(registry.TryAddPairingRoute("old", "host", clock.GetUtcNow().AddMinutes(5)));
        Assert.True(registry.TryAddPairingRoute("new", "host", clock.GetUtcNow().AddMinutes(5)));

        Assert.Null(registry.ResolvePairingRoute("old"));
        Assert.Equal("host", registry.ResolvePairingRoute("new"));
        Assert.Equal(1, registry.Snapshot().PairingRoutes);
    }

    [Fact]
    public void Registry_NewAuthenticatedConnectionsReplaceOldWithoutStaleRemoval()
    {
        var registry = new SignalConnectionRegistry(TimeProvider.System);
        var firstHost = new SignalConnection("host-old", "1.2.3.4");
        var newHost = new SignalConnection("host-new", "1.2.3.4");
        Assert.True(registry.TryReplaceHost("host", firstHost, out var replacedHost));
        Assert.Null(replacedHost);
        Assert.True(registry.TryReplaceHost("host", newHost, out replacedHost));
        Assert.Equal(firstHost, replacedHost);
        Assert.False(registry.RemoveHost("host", firstHost.Id));
        Assert.Equal(newHost, registry.GetHost("host"));

        var firstClient = new SignalConnection("client-old", "1.2.3.4");
        var newClient = new SignalConnection("client-new", "5.6.7.8");
        Assert.True(registry.TryReplaceClient("host", "device", firstClient, out var replacedClient));
        Assert.Null(replacedClient);
        Assert.True(registry.TryReplaceClient("host", "device", newClient, out replacedClient));
        Assert.Equal(firstClient, replacedClient);
        Assert.False(registry.RemoveClient("host", "device", firstClient.Id));
        Assert.Equal(newClient, registry.GetClient("host", "device"));
        Assert.True(registry.RemoveClient("host", "device", newClient.Id));
    }

    [Fact]
    public void Metrics_BoundsVersionCardinalityAndContainsNoIdentifiers()
    {
        var metrics = new SignalMetrics(TimeProvider.System);
        for (var index = 0; index < 30; index++)
            metrics.ObserveTelemetry("connected", "host", $"{index}.0");
        var rendered = metrics.Render(new SignalRegistrySnapshot(0, 0, 0));

        Assert.Contains("version=\"other\"", rendered, StringComparison.Ordinal);
        Assert.True(
            rendered.Split('\n').Count(line => line.StartsWith(
                "codex_bridge_remote_version_total{", StringComparison.Ordinal)) <= 16);
        Assert.DoesNotContain("hostId", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deviceId", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sdp", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrivacyLogger_RemovesSensitiveFields()
    {
        var sanitized = PrivacySafeLogger.Sanitize(new Dictionary<string, object?>
        {
            ["eventName"] = "connected",
            ["hostIdHash"] = "safe",
            ["sdp"] = "private",
            ["turnCredential"] = "private",
            ["signedPayload"] = "private",
        });
        var json = JsonSerializer.Serialize(sanitized);
        Assert.Contains("connected", json);
        Assert.DoesNotContain("private", json);
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}
