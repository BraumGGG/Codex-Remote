using System.Text.Json;

namespace CodexBridge.Entitlements;

public sealed class LocalEntitlementStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly EntitlementTokenVerifier _verifier;
    private Dictionary<string, string> _tokens;

    public LocalEntitlementStore(string path, EntitlementTokenVerifier verifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _tokens = Load(_path);
    }

    public EntitlementDecision Install(string token, string hostId, string deviceId)
    {
        var decision = _verifier.Verify(token, hostId, deviceId);
        if (!decision.CanSend) throw new InvalidDataException(decision.ErrorCode ?? "entitlement_not_pro");
        lock (_gate)
        {
            var previous = _tokens.TryGetValue(deviceId, out var existing) ? existing : null;
            _tokens[deviceId] = token;
            try { Persist(); }
            catch
            {
                if (previous is null) _tokens.Remove(deviceId); else _tokens[deviceId] = previous;
                throw;
            }
        }
        return decision;
    }

    public EntitlementDecision Evaluate(string hostId, string deviceId)
    {
        lock (_gate)
        {
            return _tokens.TryGetValue(deviceId, out var token)
                ? _verifier.Verify(token, hostId, deviceId)
                : EntitlementDecision.Free();
        }
    }

    public bool Remove(string deviceId)
    {
        lock (_gate)
        {
            if (!_tokens.Remove(deviceId, out var previous)) return false;
            try { Persist(); }
            catch { _tokens[deviceId] = previous; throw; }
            return true;
        }
    }

    private void Persist()
    {
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Token path has no parent.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(
                new StoredTokens(1, _tokens), JsonOptions));
            using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) stream.Flush(true);
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static Dictionary<string, string> Load(string path)
    {
        if (!File.Exists(path)) return new(StringComparer.Ordinal);
        var stored = JsonSerializer.Deserialize<StoredTokens>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Local entitlement store is empty.");
        if (stored.Version != 1) throw new InvalidDataException("Unsupported local entitlement store version.");
        return new Dictionary<string, string>(stored.Tokens, StringComparer.Ordinal);
    }

    private sealed record StoredTokens(int Version, Dictionary<string, string> Tokens);
}
