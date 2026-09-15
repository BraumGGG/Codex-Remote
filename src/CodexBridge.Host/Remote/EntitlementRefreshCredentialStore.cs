using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexBridge.Host.Remote;

public sealed record EntitlementRefreshCredential(string LicenseId, string DeviceId, string RefreshToken);

public sealed class EntitlementRefreshCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CodexBridge.EntitlementRefresh.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _path;
    private Dictionary<string, StoredCredential> _credentials;

    public EntitlementRefreshCredentialStore(string path)
    {
        _path = Path.GetFullPath(path);
        _credentials = Load(_path);
    }

    public void Put(EntitlementRefreshCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var protectedToken = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(credential.RefreshToken), Entropy, DataProtectionScope.CurrentUser);
        lock (_gate)
        {
            var previous = _credentials.TryGetValue(credential.DeviceId, out var existing) ? existing : null;
            _credentials[credential.DeviceId] = new StoredCredential(
                credential.LicenseId, credential.DeviceId, Convert.ToBase64String(protectedToken));
            try { Persist(); }
            catch
            {
                if (previous is null) _credentials.Remove(credential.DeviceId); else _credentials[credential.DeviceId] = previous;
                throw;
            }
        }
        CryptographicOperations.ZeroMemory(protectedToken);
    }

    public IReadOnlyList<EntitlementRefreshCredential> List()
    {
        lock (_gate)
        {
            return _credentials.Values.Select(item =>
            {
                var clear = ProtectedData.Unprotect(Convert.FromBase64String(item.ProtectedRefreshToken), Entropy, DataProtectionScope.CurrentUser);
                try { return new EntitlementRefreshCredential(item.LicenseId, item.DeviceId, Encoding.UTF8.GetString(clear)); }
                finally { CryptographicOperations.ZeroMemory(clear); }
            }).ToArray();
        }
    }

    public bool Remove(string deviceId)
    {
        lock (_gate)
        {
            if (!_credentials.Remove(deviceId, out var previous)) return false;
            try { Persist(); }
            catch { _credentials[deviceId] = previous; throw; }
            return true;
        }
    }

    private void Persist()
    {
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Credential path has no parent.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new StoredState(1, _credentials), JsonOptions));
            using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) stream.Flush(true);
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static Dictionary<string, StoredCredential> Load(string path)
    {
        if (!File.Exists(path)) return new(StringComparer.Ordinal);
        var state = JsonSerializer.Deserialize<StoredState>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Credential state is empty.");
        if (state.Version != 1) throw new InvalidDataException("Unsupported credential state version.");
        return new Dictionary<string, StoredCredential>(state.Credentials, StringComparer.Ordinal);
    }

    private sealed record StoredState(int Version, Dictionary<string, StoredCredential> Credentials);
    private sealed record StoredCredential(string LicenseId, string DeviceId, string ProtectedRefreshToken);
}
