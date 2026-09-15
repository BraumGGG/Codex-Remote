using System.Security.Cryptography;
using CodexBridge.Entitlement.Server;
using CodexBridge.Entitlements;

namespace CodexBridge.Entitlements.Tests;

public sealed class EntitlementRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Directory.GetCurrentDirectory(), ".test-state", "entitlements", Guid.NewGuid().ToString("N"));

    [Fact]
    public void NewRepository_PersistsAnEmptyRecoverableState()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-08-17T00:00:00Z"));

        using (CreateRepository(key, clock)) { }

        var path = Path.Combine(_directory, "state.json");
        using var state = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1, state.RootElement.GetProperty("version").GetInt32());
        Assert.Empty(state.RootElement.GetProperty("invites").EnumerateArray());
        Assert.Empty(state.RootElement.GetProperty("licenses").EnumerateArray());
    }

    [Fact]
    public void InviteRedeemAndRefresh_IssuesBoundProWithoutStoringSecrets()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-08-17T00:00:00Z"));
        using var repository = CreateRepository(key, clock);
        var invite = repository.CreateInvite("pro", 30, 1, clock.GetUtcNow().AddDays(1));
        var credential = repository.Redeem(invite.Code, "user-1", "host-1", "device-1");
        var refreshed = repository.Refresh(
            credential.LicenseId, credential.RefreshToken, "host-1", "device-1");
        var verifier = Verifier(key, clock);

        Assert.True(verifier.Verify(refreshed, "host-1", "device-1").CanSend);
        Assert.Throws<EntitlementException>(() => repository.Redeem(
            invite.Code, "user-1", "host-1", "device-2"));
        var persisted = File.ReadAllText(Path.Combine(_directory, "state.json"));
        Assert.DoesNotContain(invite.Code, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(credential.RefreshToken, persisted, StringComparison.Ordinal);
        var audit = File.ReadAllText(Path.Combine(_directory, "audit.jsonl"));
        Assert.DoesNotContain(invite.Code, audit, StringComparison.Ordinal);
        Assert.DoesNotContain(credential.RefreshToken, audit, StringComparison.Ordinal);
        Assert.DoesNotContain(credential.EntitlementToken, audit, StringComparison.Ordinal);
    }

    [Fact]
    public void GrantEnforcesHostAndDeviceLimits_AndRevocationStopsRefresh()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-08-17T00:00:00Z"));
        using var repository = CreateRepository(key, clock, maximumHosts: 1, maximumDevices: 1);
        var first = repository.Grant(
            "user-1", "host-1", "device-1", "pro", clock.GetUtcNow().AddDays(30));

        Assert.Equal("host_limit_reached", Assert.Throws<EntitlementException>(() => repository.Grant(
            "user-1", "host-2", "device-1", "pro", clock.GetUtcNow().AddDays(30))).ErrorCode);
        Assert.Equal("device_limit_reached", Assert.Throws<EntitlementException>(() => repository.Grant(
            "user-1", "host-1", "device-2", "pro", clock.GetUtcNow().AddDays(30))).ErrorCode);
        Assert.True(repository.Revoke(first.LicenseId));
        Assert.Equal("license_revoked", Assert.Throws<EntitlementException>(() => repository.Refresh(
            first.LicenseId, first.RefreshToken, "host-1", "device-1")).ErrorCode);
    }

    [Fact]
    public void WrongRefreshTokenAndBindingAreRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-08-17T00:00:00Z"));
        using var repository = CreateRepository(key, clock);
        var grant = repository.Grant("user-1", "host-1", "device-1", "pro", clock.GetUtcNow().AddDays(30));

        Assert.Equal("refresh_unauthorized", Assert.Throws<EntitlementException>(() => repository.Refresh(
            grant.LicenseId, "wrong", "host-1", "device-1")).ErrorCode);
        Assert.Equal("license_binding_mismatch", Assert.Throws<EntitlementException>(() => repository.Refresh(
            grant.LicenseId, grant.RefreshToken, "host-2", "device-1")).ErrorCode);
    }

    [Fact]
    public void InviteSummary_ShowsEncryptedCodeAndRedemptionAudit()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-08-17T00:00:00Z"));
        using var repository = CreateRepository(key, clock);
        var invite = repository.CreateInvite("free", 30, 1, clock.GetUtcNow().AddDays(7), "fan@qq.com");
        var before = Assert.Single(repository.ListInvites());
        Assert.Equal(invite.Code, before.Code);
        Assert.Equal("fan@qq.com", before.RecipientEmail);
        Assert.Equal(0, before.Redemptions);
        repository.Redeem(invite.Code, "user-1", "host-1", "device-1");
        var after = Assert.Single(repository.ListInvites());
        Assert.Equal(1, after.Redemptions);
        Assert.Equal("user-1", after.RedeemedByUserId);
        Assert.NotNull(after.RedeemedAt);
    }

    private EntitlementRepository CreateRepository(
        ECDsa key,
        TimeProvider clock,
        int maximumHosts = 1,
        int maximumDevices = 3)
    {
        Directory.CreateDirectory(_directory);
        return new EntitlementRepository(new EntitlementServerOptions(
            Path.Combine(_directory, "state.json"),
            Path.Combine(_directory, "audit.jsonl"),
            "key-1",
            Encode(key.ExportPkcs8PrivateKey()),
            new string('A', 32),
            maximumHosts,
            maximumDevices), clock);
    }

    private static EntitlementTokenVerifier Verifier(ECDsa key, TimeProvider clock) => new(
        new Dictionary<string, string> { ["key-1"] = Encode(key.ExportSubjectPublicKeyInfo()) }, clock);
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
