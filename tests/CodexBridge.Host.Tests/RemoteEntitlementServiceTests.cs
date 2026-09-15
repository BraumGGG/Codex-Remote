using System.Net;
using System.Security.Cryptography;
using CodexBridge.Entitlements;
using CodexBridge.Host.Auth;
using CodexBridge.Host.Remote;

namespace CodexBridge.Host.Tests;

public sealed class RemoteEntitlementServiceTests : IDisposable
{
    private readonly string _directory = TestPaths.CreateDirectory("remote-entitlements");

    [Fact]
    public async Task RedeemPersistsProtectedRefresh_AndCloudRevocationImmediatelyRemovesPro()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var clock = TimeProvider.System;
        var identityStore = new RemoteIdentityStore(Path.Combine(_directory, "identity.json"));
        using var identity = await identityStore.GetOrCreateAsync();
        var cloud = new FakeCloud(signingKey, identity.HostId, clock);
        var localTokens = new LocalEntitlementStore(
            Path.Combine(_directory, "tokens.json"),
            new EntitlementTokenVerifier(new Dictionary<string, string>
            {
                ["key-1"] = Encode(signingKey.ExportSubjectPublicKeyInfo()),
            }, clock));
        var credentialsPath = Path.Combine(_directory, "credentials.json");
        var service = new RemoteEntitlementService(
            localTokens,
            identityStore,
            new EntitlementRefreshCredentialStore(credentialsPath),
            cloud);
        service.Initialize(identity.HostId);

        var decision = await service.RedeemAsync("invite-secret", "user-1", "device-1");
        Assert.True(decision.CanSend);
        Assert.True(service.Resolve(new DevicePrincipal("device-1", "phone", false)).CanSend);
        Assert.DoesNotContain("refresh-secret", await File.ReadAllTextAsync(credentialsPath), StringComparison.Ordinal);
        Assert.DoesNotContain("invite-secret", await File.ReadAllTextAsync(credentialsPath), StringComparison.Ordinal);

        cloud.Revoked = true;
        await service.RefreshAllAsync();

        Assert.False(service.Resolve(new DevicePrincipal("device-1", "phone", true)).CanSend);
        Assert.Empty(new EntitlementRefreshCredentialStore(credentialsPath).List());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class FakeCloud(ECDsa key, string hostId, TimeProvider clock) : IEntitlementCloudClient
    {
        public bool Revoked { get; set; }

        public Task<CloudLicenseCredential> RedeemAsync(
            string code, string userId, string requestedHostId, string deviceId, CancellationToken cancellationToken)
        {
            Assert.Equal(hostId, requestedHostId);
            return Task.FromResult(new CloudLicenseCredential(
                "license-1", "refresh-secret", Issue(userId, deviceId)));
        }

        public Task<string> RefreshAsync(
            string licenseId, string refreshToken, string requestedHostId, string deviceId, CancellationToken cancellationToken)
        {
            if (Revoked) return Task.FromException<string>(new EntitlementCloudException("license_revoked", HttpStatusCode.Forbidden));
            return Task.FromResult(Issue("user-1", deviceId));
        }

        private string Issue(string userId, string deviceId)
        {
            var now = clock.GetUtcNow();
            return new EntitlementTokenSigner(key, "key-1").Sign(new EntitlementGrant(
                1, "key-1", "license-1", userId, hostId, deviceId, "pro",
                now, now.AddMinutes(15), now.AddMinutes(30), now.AddHours(72)));
        }
    }
}
