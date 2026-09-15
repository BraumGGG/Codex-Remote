using System.Security.Cryptography;
using System.Text;

namespace CodexBridge.Signal;

public sealed record TurnCredentials(string Username, string Credential, DateTimeOffset ExpiresAt);

public sealed class TurnCredentialService(byte[] sharedSecret, TimeProvider timeProvider)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly byte[] _sharedSecret = sharedSecret is { Length: >= 32 }
        ? sharedSecret.ToArray()
        : throw new ArgumentException("TURN shared secret must be at least 256 bits.", nameof(sharedSecret));

    public TurnCredentials Issue(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (deviceId.Length > 128 || deviceId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new ArgumentException("Device ID is invalid.", nameof(deviceId));
        var expiresAt = timeProvider.GetUtcNow() + Lifetime;
        // Keep the REST username format while making overlapping connection
        // generations distinct, so stale allocations cannot share coturn's
        // per-user quota with a fresh cold-start connection.
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var username = $"{expiresAt.ToUnixTimeSeconds()}:{deviceId}:{nonce}";
        var credential = Convert.ToBase64String(HMACSHA1.HashData(
            _sharedSecret,
            Encoding.UTF8.GetBytes(username)));
        return new TurnCredentials(username, credential, expiresAt);
    }
}
