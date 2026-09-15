using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexBridge.Host.Remote;

public sealed class RemoteHostIdentity : IDisposable
{
    private readonly ECDsa _key;

    internal RemoteHostIdentity(string hostId, string publicKeySpki, ECDsa key)
    {
        HostId = hostId;
        PublicKeySpki = publicKeySpki;
        _key = key;
    }

    public string HostId { get; }
    public string PublicKeySpki { get; }

    public byte[] Sign(ReadOnlySpan<byte> payload) =>
        _key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public static bool Verify(string publicKeySpki, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature)
    {
        try
        {
            using var key = ECDsa.Create();
            var publicKey = RemoteEncoding.Base64UrlDecode(publicKeySpki);
            key.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
            if (bytesRead != publicKey.Length)
            {
                return false;
            }

            return key.VerifyData(
                payload,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public void Dispose() => _key.Dispose();
}

public sealed class RemoteIdentityStore
{
    private static readonly byte[] ProtectionEntropy = Encoding.UTF8.GetBytes("CodexBridge.RemoteIdentity.v1");
    private readonly string _path;
    private readonly Func<ECDsa> _keyFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RemoteIdentityStore(string path, Func<ECDsa>? keyFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _keyFactory = keyFactory ?? (() => ECDsa.Create(ECCurve.NamedCurves.nistP256));
    }

    public async Task<RemoteHostIdentity> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return File.Exists(_path)
                ? await LoadAsync(cancellationToken).ConfigureAwait(false)
                : await CreateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RemoteHostIdentity> CreateAsync(CancellationToken cancellationToken)
    {
        using var sourceKey = _keyFactory();
        var privateKey = sourceKey.ExportPkcs8PrivateKey();
        var publicKey = sourceKey.ExportSubjectPublicKeyInfo();
        try
        {
            var protectedPrivateKey = ProtectedData.Protect(
                privateKey,
                ProtectionEntropy,
                DataProtectionScope.CurrentUser);
            var stored = new StoredRemoteIdentity(
                1,
                RemoteEncoding.Base64UrlEncode(publicKey),
                RemoteEncoding.Base64UrlEncode(protectedPrivateKey));
            await PersistAsync(stored, cancellationToken).ConfigureAwait(false);
            return ImportIdentity(publicKey, privateKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    private async Task<RemoteHostIdentity> LoadAsync(CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
        var stored = JsonSerializer.Deserialize<StoredRemoteIdentity>(json)
            ?? throw new InvalidDataException("Remote identity file is empty.");
        if (stored.Version != 1)
        {
            throw new InvalidDataException("Unsupported remote identity version.");
        }

        var publicKey = RemoteEncoding.Base64UrlDecode(stored.PublicKeySpki);
        var protectedPrivateKey = RemoteEncoding.Base64UrlDecode(stored.ProtectedPrivateKey);
        var privateKey = ProtectedData.Unprotect(
            protectedPrivateKey,
            ProtectionEntropy,
            DataProtectionScope.CurrentUser);
        try
        {
            return ImportIdentity(publicKey, privateKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    private static RemoteHostIdentity ImportIdentity(byte[] expectedPublicKey, byte[] privateKey)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(privateKey, out var bytesRead);
            if (bytesRead != privateKey.Length)
            {
                throw new InvalidDataException("Remote private key contains trailing data.");
            }

            var actualPublicKey = key.ExportSubjectPublicKeyInfo();
            if (!CryptographicOperations.FixedTimeEquals(actualPublicKey, expectedPublicKey))
            {
                throw new InvalidDataException("Remote identity public and private keys do not match.");
            }

            var publicKeySpki = RemoteEncoding.Base64UrlEncode(actualPublicKey);
            var hostId = RemoteEncoding.Base64UrlEncode(SHA256.HashData(actualPublicKey));
            return new RemoteHostIdentity(hostId, publicKeySpki, key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private async Task PersistAsync(StoredRemoteIdentity identity, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Remote identity path has no parent directory.");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                tempPath,
                JsonSerializer.Serialize(identity),
                cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, _path, overwrite: false);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private sealed record StoredRemoteIdentity(
        int Version,
        string PublicKeySpki,
        string ProtectedPrivateKey);
}
