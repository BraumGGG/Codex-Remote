using System.Security.Cryptography;
using CodexBridge.Entitlements;

namespace CodexBridge.Entitlements.Tests;

public sealed class EntitlementTokenTests
{
    [Fact]
    public void SignedProToken_IsBoundToHostDeviceAndTime()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-08-17T00:00:00Z"));
        var token = new EntitlementTokenSigner(key, "key-1").Sign(Grant(clock.GetUtcNow()));
        var verifier = CreateVerifier(key, clock);

        Assert.Equal(EntitlementState.Pro, verifier.Verify(token, "host-1", "device-1").State);
        Assert.Equal("entitlement_binding_mismatch", verifier.Verify(token, "host-2", "device-1").ErrorCode);
        Assert.Equal("entitlement_binding_mismatch", verifier.Verify(token, "host-1", "device-2").ErrorCode);
    }

    [Fact]
    public void TamperingExpiryOrPlan_InvalidatesSignature()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-08-17T00:00:00Z"));
        var token = new EntitlementTokenSigner(key, "key-1").Sign(Grant(clock.GetUtcNow()));
        var parts = token.Split('.');
        var payload = System.Text.Encoding.UTF8.GetString(Decode(parts[0])).Replace("\"pro\"", "\"free\"", StringComparison.Ordinal);
        var tampered = Encode(System.Text.Encoding.UTF8.GetBytes(payload)) + "." + parts[1];

        Assert.Equal("entitlement_invalid_signature", CreateVerifier(key, clock).Verify(tampered, "host-1", "device-1").ErrorCode);
    }

    [Fact]
    public void ExpiredOnlineToken_UsesBoundedOfflineGraceThenBecomesReadOnly()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-08-17T00:00:00Z"));
        var token = new EntitlementTokenSigner(key, "key-1").Sign(Grant(clock.GetUtcNow()));
        var verifier = CreateVerifier(key, clock);

        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(EntitlementState.OfflineGrace, verifier.Verify(token, "host-1", "device-1").State);
        clock.Advance(TimeSpan.FromHours(72));
        var expired = verifier.Verify(token, "host-1", "device-1");
        Assert.False(expired.CanSend);
        Assert.Equal(EntitlementState.Expired, expired.State);
    }

    [Fact]
    public void InvalidAndOversizedTokens_CloseToReadOnly()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var verifier = CreateVerifier(key, clock);
        Assert.False(verifier.Verify(null, "host", "device").CanSend);
        Assert.False(verifier.Verify(new string('A', 9000) + ".AA", "host", "device").CanSend);
        Assert.False(verifier.Verify("not.a.valid.token", "host", "device").CanSend);
    }

    [Fact]
    public void LocalStore_PersistsOnlyValidBoundTokenAndRemovalImmediatelyDisablesPro()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-08-17T00:00:00Z"));
        var token = new EntitlementTokenSigner(key, "key-1").Sign(Grant(clock.GetUtcNow()));
        var directory = Path.Combine(Directory.GetCurrentDirectory(), ".test-state", "local-entitlement", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "tokens.json");
        try
        {
            var store = new LocalEntitlementStore(path, CreateVerifier(key, clock));
            Assert.True(store.Install(token, "host-1", "device-1").CanSend);
            Assert.Throws<InvalidDataException>(() => store.Install(token, "host-2", "device-1"));
            Assert.True(new LocalEntitlementStore(path, CreateVerifier(key, clock)).Evaluate("host-1", "device-1").CanSend);
            Assert.True(store.Remove("device-1"));
            Assert.False(store.Evaluate("host-1", "device-1").CanSend);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static EntitlementGrant Grant(DateTimeOffset now) => new(
        1, "key-1", "license-1", "user-1", "host-1", "device-1", "pro",
        now, now.AddMinutes(15), now.AddMinutes(30), now.AddHours(72));

    private static EntitlementTokenVerifier CreateVerifier(ECDsa key, TimeProvider clock) => new(
        new Dictionary<string, string> { ["key-1"] = Encode(key.ExportSubjectPublicKeyInfo()) }, clock);

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Decode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += padded.Length % 4 == 2 ? "==" : padded.Length % 4 == 3 ? "=" : "";
        return Convert.FromBase64String(padded);
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
