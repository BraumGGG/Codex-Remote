using System.Net;
using System.Security.Cryptography;

namespace CodexBridge.Host.Auth;

public sealed class ManagementAccessGuard
{
    public const string TokenEnvironmentVariable = "CODEX_BRIDGE_MANAGEMENT_TOKEN";
    public const string TokenHeader = "X-CodexBridge-Management-Token";
    public const string NonceHeader = "X-CodexBridge-Management-Nonce";
    private const int MaximumRememberedNonces = 2048;

    private readonly byte[] _token = LoadTokenFromEnvironment() ?? RandomNumberGenerator.GetBytes(32);
    private readonly object _gate = new();
    private readonly HashSet<string> _nonces = new(StringComparer.Ordinal);
    private readonly Queue<string> _nonceOrder = new();

    public string Token => Convert.ToBase64String(_token);

    private static byte[]? LoadTokenFromEnvironment()
    {
        var configuredToken = Environment.GetEnvironmentVariable(TokenEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredToken)) return null;
        try
        {
            var token = Convert.FromBase64String(configuredToken);
            return token.Length == 32 ? token : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public bool Authorize(
        IPAddress? remoteAddress,
        IPAddress? localAddress,
        string? suppliedToken,
        string? nonce)
    {
        if (remoteAddress is null ||
            localAddress is null ||
            !IPAddress.IsLoopback(remoteAddress) ||
            !IPAddress.IsLoopback(localAddress) ||
            string.IsNullOrWhiteSpace(suppliedToken) ||
            string.IsNullOrWhiteSpace(nonce) ||
            nonce.Length is < 16 or > 128)
        {
            return false;
        }

        byte[] candidate;
        try
        {
            candidate = Convert.FromBase64String(suppliedToken);
        }
        catch (FormatException)
        {
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(_token, candidate))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_nonces.Add(nonce)) return false;
            _nonceOrder.Enqueue(nonce);
            while (_nonceOrder.Count > MaximumRememberedNonces)
            {
                _nonces.Remove(_nonceOrder.Dequeue());
            }

            return true;
        }
    }
}
