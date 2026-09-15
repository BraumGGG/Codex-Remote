using System.Security.Cryptography;

namespace CodexBridge.Host.Remote;

public enum RemotePairingConsumeResult
{
    Success,
    Invalid,
    Expired,
    Used,
    NotIssued,
}

public sealed record IssuedRemotePairing(string RouteId, string Secret, DateTimeOffset ExpiresAt);

public sealed class RemotePairingService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, PairingState> _pairings = new(StringComparer.Ordinal);

    public RemotePairingService(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public IssuedRemotePairing Issue()
    {
        var routeId = RemoteEncoding.Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
        var secret = RandomNumberGenerator.GetBytes(16);
        var expiresAt = _timeProvider.GetUtcNow() + Lifetime;
        lock (_gate)
        {
            PruneExpired();
            _pairings.Add(routeId, new PairingState(secret, expiresAt, Consumed: false));
        }

        return new IssuedRemotePairing(routeId, RemoteEncoding.Base64UrlEncode(secret), expiresAt);
    }

    public bool IsExpired(IssuedRemotePairing pairing) =>
        _timeProvider.GetUtcNow() >= pairing.ExpiresAt;

    public static string CreateProof(string secret, ReadOnlySpan<byte> payload)
    {
        var secretBytes = RemoteEncoding.Base64UrlDecode(secret);
        try
        {
            return RemoteEncoding.Base64UrlEncode(HMACSHA256.HashData(secretBytes, payload));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    public RemotePairingConsumeResult Consume(string routeId, ReadOnlySpan<byte> payload, string proof)
    {
        if (string.IsNullOrWhiteSpace(routeId) || string.IsNullOrWhiteSpace(proof))
        {
            return RemotePairingConsumeResult.Invalid;
        }

        lock (_gate)
        {
            if (!_pairings.TryGetValue(routeId, out var pairing))
            {
                return RemotePairingConsumeResult.NotIssued;
            }

            if (pairing.Consumed)
            {
                return RemotePairingConsumeResult.Used;
            }

            if (_timeProvider.GetUtcNow() >= pairing.ExpiresAt)
            {
                return RemotePairingConsumeResult.Expired;
            }

            byte[] suppliedProof;
            try
            {
                suppliedProof = RemoteEncoding.Base64UrlDecode(proof);
            }
            catch (FormatException)
            {
                return RemotePairingConsumeResult.Invalid;
            }

            var expectedProof = HMACSHA256.HashData(pairing.Secret, payload);
            if (!CryptographicOperations.FixedTimeEquals(expectedProof, suppliedProof))
            {
                return RemotePairingConsumeResult.Invalid;
            }

            _pairings[routeId] = pairing with { Consumed = true };
            return RemotePairingConsumeResult.Success;
        }
    }

    private void PruneExpired()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var routeId in _pairings
                     .Where(item => now >= item.Value.ExpiresAt + Lifetime)
                     .Select(item => item.Key)
                     .ToArray())
        {
            _pairings.Remove(routeId);
        }
    }

    private sealed record PairingState(byte[] Secret, DateTimeOffset ExpiresAt, bool Consumed);
}
