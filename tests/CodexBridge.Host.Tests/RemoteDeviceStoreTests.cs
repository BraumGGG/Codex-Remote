using System.Security.Cryptography;
using CodexBridge.Host.Remote;

namespace CodexBridge.Host.Tests;

public sealed class RemoteDeviceStoreTests : IDisposable
{
    private readonly string _directory = TestPaths.CreateDirectory("remote-device");

    [Fact]
    public async Task DeviceKeyBindsIdentityPermissionAndRoutingTicket()
    {
        Directory.CreateDirectory(_directory);
        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = RemoteEncoding.Base64UrlEncode(deviceKey.ExportSubjectPublicKeyInfo());
        var store = new RemoteDeviceStore(Path.Combine(_directory, "devices.json"), TimeProvider.System);
        var principal = store.Register("Android", publicKey, canSend: false);
        Assert.False(store.Authenticate(principal.DeviceId)!.CanSend);
        Assert.Equal(publicKey, store.Find(principal.DeviceId)!.PublicKeySpki);

        using var host = await new RemoteIdentityStore(Path.Combine(_directory, "host.json")).GetOrCreateAsync();
        var ticket = new RemoteRoutingTicketIssuer(TimeProvider.System).Issue(host, store.Find(principal.DeviceId)!);
        Assert.True(new SignedSignalPayload(ticket.PayloadBase64Url, ticket.SignatureBase64Url)
            .Verify(host.PublicKeySpki));
    }

    [Fact]
    public void RejectsNonP256AndDuplicateDevice()
    {
        Directory.CreateDirectory(_directory);
        var store = new RemoteDeviceStore(Path.Combine(_directory, "devices.json"), TimeProvider.System);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = RemoteEncoding.Base64UrlEncode(key.ExportSubjectPublicKeyInfo());
        store.Register("Android", publicKey, false);
        Assert.Throws<InvalidOperationException>(() => store.Register("Again", publicKey, true));
        Assert.Throws<InvalidDataException>(() => store.Register("Bad", "AA", false));
    }

    [Fact]
    public void PairingSameDeviceAgain_ReusesIdentityWithoutCreatingDuplicate()
    {
        var store = new RemoteDeviceStore(Path.Combine(_directory, "devices.json"), TimeProvider.System);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = RemoteEncoding.Base64UrlEncode(key.ExportSubjectPublicKeyInfo());

        var first = store.RegisterForPairing("Android", publicKey, false);
        var second = store.RegisterForPairing("Android after reinstall", publicKey, false);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Principal.DeviceId, second.Principal.DeviceId);
        Assert.Single(store.List());
    }

    [Fact]
    public void PairingExistingKeyWithInstallationId_AdoptsInstallationWithoutCreatingDuplicate()
    {
        var store = new RemoteDeviceStore(Path.Combine(_directory, "devices.json"), TimeProvider.System);
        var publicKey = CreatePublicKey();
        store.RegisterForPairing("Android", publicKey, true);
        var installationId = "ABCD1234EFGH5678IJKL9012MNOP3456QRSTUVWXyz0";

        var adopted = store.RegisterForPairing("Android", publicKey, false, installationId);

        Assert.False(adopted.Created);
        var device = Assert.Single(store.List());
        Assert.Equal(installationId, device.InstallationId);
        Assert.True(device.CanSend);
    }

    [Fact]
    public void PairingSameInstallationWithNewKey_ReplacesOldDeviceAndKeepsPermission()
    {
        var store = new RemoteDeviceStore(Path.Combine(_directory, "devices.json"), TimeProvider.System);
        var installationId = "ABCD1234EFGH5678IJKL9012MNOP3456QRSTUVWXyz0";
        var old = store.RegisterForPairing("Android", CreatePublicKey(), true, installationId);
        string? revokedDeviceId = null;
        store.DeviceRevoked += deviceId => revokedDeviceId = deviceId;

        var replacement = store.RegisterForPairing("Android", CreatePublicKey(), false, installationId);

        var device = Assert.Single(store.List());
        Assert.False(replacement.Created);
        Assert.Equal(old.Principal.DeviceId, replacement.ReplacedDevice!.DeviceId);
        Assert.Equal(replacement.Principal.DeviceId, device.DeviceId);
        Assert.True(device.CanSend);
        Assert.Equal(installationId, device.InstallationId);
        Assert.Equal(old.Principal.DeviceId, revokedDeviceId);
    }

    [Fact]
    public void PairingDifferentInstallationsWithNewKeys_KeepsBothDevices()
    {
        var store = new RemoteDeviceStore(Path.Combine(_directory, "devices.json"), TimeProvider.System);

        store.RegisterForPairing("Android", CreatePublicKey(), true, "ABCD1234EFGH5678IJKL9012MNOP3456QRSTUVWXyz0");
        var second = store.RegisterForPairing("Android", CreatePublicKey(), true, "ZYXWVUTSRQPO5432NMLK1098JIHGFEDCBA987654321");

        Assert.True(second.Created);
        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void PairingLegacyDeviceWithoutInstallationId_DoesNotReplaceIt()
    {
        var store = new RemoteDeviceStore(Path.Combine(_directory, "devices.json"), TimeProvider.System);
        store.RegisterForPairing("Android", CreatePublicKey(), true);

        var second = store.RegisterForPairing(
            "Android", CreatePublicKey(), true, "ABCD1234EFGH5678IJKL9012MNOP3456QRSTUVWXyz0");

        Assert.True(second.Created);
        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void RollbackPairingAfterReplacement_RestoresOldDevice()
    {
        var store = new RemoteDeviceStore(Path.Combine(_directory, "devices.json"), TimeProvider.System);
        var installationId = "ABCD1234EFGH5678IJKL9012MNOP3456QRSTUVWXyz0";
        var old = store.RegisterForPairing("Android", CreatePublicKey(), true, installationId);
        var replacement = store.RegisterForPairing("Android", CreatePublicKey(), false, installationId);

        store.RollbackPairing(replacement);

        var restored = Assert.Single(store.List());
        Assert.Equal(old.Principal.DeviceId, restored.DeviceId);
        Assert.True(restored.CanSend);
        Assert.Equal(installationId, restored.InstallationId);
    }

    [Fact]
    public void ListAndRevoke_InvalidatesDeviceAndRaisesRevocationEvent()
    {
        var store = new RemoteDeviceStore(
            Path.Combine(_directory, "devices.json"),
            TimeProvider.System);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var principal = store.Register(
            "Fake Remote Phone",
            RemoteEncoding.Base64UrlEncode(key.ExportSubjectPublicKeyInfo()),
            false);
        string? revoked = null;
        store.DeviceRevoked += deviceId => revoked = deviceId;

        Assert.Single(store.List());
        Assert.True(store.Revoke(principal.DeviceId));
        Assert.Equal(principal.DeviceId, revoked);
        Assert.Null(store.Authenticate(principal.DeviceId));
        Assert.False(store.Revoke(principal.DeviceId));
        Assert.Empty(store.List());
    }

    [Fact]
    public void Revoke_CancelsActiveSessionLeaseImmediately()
    {
        var store = new RemoteDeviceStore(
            Path.Combine(_directory, "devices.json"),
            TimeProvider.System);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var principal = store.Register(
            "Active Fake Phone",
            RemoteEncoding.Base64UrlEncode(key.ExportSubjectPublicKeyInfo()),
            false);
        using var lease = new RemoteDeviceSessionLease(
            store,
            principal.DeviceId,
            CancellationToken.None);

        Assert.False(lease.Token.IsCancellationRequested);
        Assert.True(store.Revoke(principal.DeviceId));
        Assert.True(lease.Token.IsCancellationRequested);
    }

    [Fact]
    public void Revoke_RollsBackMemoryAndDoesNotRaiseEventWhenPersistFails()
    {
        var path = Path.Combine(_directory, "devices.json");
        var store = new RemoteDeviceStore(path, TimeProvider.System);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var principal = store.Register(
            "Rollback Remote Phone",
            RemoteEncoding.Base64UrlEncode(key.ExportSubjectPublicKeyInfo()),
            false);
        var eventCount = 0;
        store.DeviceRevoked += _ => eventCount++;
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.ThrowsAny<Exception>(() => store.Revoke(principal.DeviceId));
            Assert.Equal(principal.DeviceId, Assert.Single(store.List()).DeviceId);
            Assert.Equal(0, eventCount);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Assert.True(store.Revoke(principal.DeviceId));
        Assert.Equal(1, eventCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private static string CreatePublicKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return RemoteEncoding.Base64UrlEncode(key.ExportSubjectPublicKeyInfo());
    }
}
