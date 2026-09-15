using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexBridge.Signal;

public sealed record RoutingTicketPayload(
    int Version,
    string HostId,
    string DeviceId,
    string DevicePublicKeySpki,
    long ExpiresAtUnixSeconds,
    string Nonce);

public sealed record SignedRoutingTicket(
    string PayloadBase64Url,
    string SignatureBase64Url,
    string HostPublicKeySpki);

public sealed record VerifiedRoutingTicket(
    string HostId,
    string DeviceId,
    string DevicePublicKeySpki,
    DateTimeOffset ExpiresAt);

public sealed class RoutingTicketVerifier(TimeProvider timeProvider)
{
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(31);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public VerifiedRoutingTicket Verify(SignedRoutingTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        try
        {
            return VerifyCore(ticket);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or ArgumentException)
        {
            throw new InvalidDataException("Routing ticket is invalid.", exception);
        }
    }

    private VerifiedRoutingTicket VerifyCore(SignedRoutingTicket ticket)
    {
        byte[] payloadBytes;
        byte[] signature;
        byte[] hostPublicKey;
        try
        {
            payloadBytes = SignalEncoding.Decode(ticket.PayloadBase64Url);
            signature = SignalEncoding.Decode(ticket.SignatureBase64Url);
            hostPublicKey = SignalEncoding.Decode(ticket.HostPublicKeySpki);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Routing ticket encoding is invalid.", exception);
        }
        if (payloadBytes.Length > 4096 || signature.Length != 64)
            throw new InvalidDataException("Routing ticket exceeds its limits.");

        RoutingTicketPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<RoutingTicketPayload>(payloadBytes, JsonOptions)
                ?? throw new InvalidDataException("Routing ticket payload is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Routing ticket payload is invalid.", exception);
        }

        if (payload.Version != 1 || string.IsNullOrWhiteSpace(payload.HostId) ||
            string.IsNullOrWhiteSpace(payload.DeviceId) || string.IsNullOrWhiteSpace(payload.DevicePublicKeySpki) ||
            SignalEncoding.Decode(payload.Nonce).Length != 16)
            throw new InvalidDataException("Routing ticket payload fields are invalid.");

        using var hostKey = ECDsa.Create();
        hostKey.ImportSubjectPublicKeyInfo(hostPublicKey, out var read);
        if (read != hostPublicKey.Length ||
            hostKey.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value ||
            !hostKey.VerifyData(
                payloadBytes,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new InvalidDataException("Routing ticket signature is invalid.");
        var expectedHostId = SignalEncoding.Encode(SHA256.HashData(hostPublicKey));
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(expectedHostId),
                System.Text.Encoding.ASCII.GetBytes(payload.HostId)))
            throw new InvalidDataException("Routing ticket host identity is invalid.");
        ValidateDeviceKey(payload.DevicePublicKeySpki);

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAtUnixSeconds);
        var now = timeProvider.GetUtcNow();
        if (expiresAt <= now || expiresAt > now + MaximumLifetime)
            throw new InvalidDataException("Routing ticket has expired or exceeds its lifetime.");
        return new VerifiedRoutingTicket(payload.HostId, payload.DeviceId, payload.DevicePublicKeySpki, expiresAt);
    }

    public static SignedRoutingTicket Sign(RoutingTicketPayload payload, ECDsa hostKey)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        return new SignedRoutingTicket(
            SignalEncoding.Encode(bytes),
            SignalEncoding.Encode(hostKey.SignData(
                bytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
            SignalEncoding.Encode(hostKey.ExportSubjectPublicKeyInfo()));
    }

    private static void ValidateDeviceKey(string spki)
    {
        using var key = ECDsa.Create();
        var bytes = SignalEncoding.Decode(spki);
        key.ImportSubjectPublicKeyInfo(bytes, out var read);
        if (read != bytes.Length || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            throw new InvalidDataException("Routing ticket device key is invalid.");
    }
}
