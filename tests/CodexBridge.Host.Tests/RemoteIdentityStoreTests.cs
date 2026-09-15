using System.Security.Cryptography;
using CodexBridge.Host.Remote;

namespace CodexBridge.Host.Tests;

public sealed class RemoteIdentityStoreTests : IDisposable
{
    private readonly string _directory = TestPaths.CreateDirectory("remote-identity");

    [Fact]
    public async Task GetOrCreate_ProtectsPrivateKeyAndRestoresStableIdentity()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "identity.json");
        using var sourceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = sourceKey.ExportPkcs8PrivateKey();
        var store = new RemoteIdentityStore(path, () => ImportPrivateKey(privateKey));

        using var first = await store.GetOrCreateAsync();
        var storedJson = await File.ReadAllTextAsync(path);
        using var second = await new RemoteIdentityStore(path).GetOrCreateAsync();

        Assert.DoesNotContain(Convert.ToBase64String(privateKey), storedJson, StringComparison.Ordinal);
        Assert.Contains("ProtectedPrivateKey", storedJson, StringComparison.Ordinal);
        Assert.Equal(first.HostId, second.HostId);
        Assert.Equal(first.PublicKeySpki, second.PublicKeySpki);

        var payload = "signed by restored identity"u8.ToArray();
        var signature = second.Sign(payload);
        Assert.True(RemoteHostIdentity.Verify(second.PublicKeySpki, payload, signature));
    }

    [Fact]
    public async Task GetOrCreate_RejectsCorruptProtectedKey()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "identity.json");
        await File.WriteAllTextAsync(
            path,
            "{\"Version\":1,\"PublicKeySpki\":\"bad\",\"ProtectedPrivateKey\":\"bad\"}");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new RemoteIdentityStore(path).GetOrCreateAsync());
    }

    private static ECDsa ImportPrivateKey(byte[] privateKey)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(privateKey, out _);
        return key;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
