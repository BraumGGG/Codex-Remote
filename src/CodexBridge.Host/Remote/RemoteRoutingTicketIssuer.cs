using System.Text.Json;
using System.Security.Cryptography;

namespace CodexBridge.Host.Remote;

public sealed record HostRoutingTicketPayload(
    int Version,
    string HostId,
    string DeviceId,
    string DevicePublicKeySpki,
    long ExpiresAtUnixSeconds,
    string Nonce);

public sealed record HostSignedRoutingTicket(
    string PayloadBase64Url,
    string SignatureBase64Url,
    string HostPublicKeySpki);

public sealed class RemoteRoutingTicketIssuer(TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public HostSignedRoutingTicket Issue(RemoteHostIdentity host, RemoteDevice device)
    {
        var payload = new HostRoutingTicketPayload(
            1,
            host.HostId,
            device.DeviceId,
            device.PublicKeySpki,
            timeProvider.GetUtcNow().AddDays(30).ToUnixTimeSeconds(),
            RemoteEncoding.Base64UrlEncode(RandomNumberGenerator.GetBytes(16)));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        return new HostSignedRoutingTicket(
            RemoteEncoding.Base64UrlEncode(bytes),
            RemoteEncoding.Base64UrlEncode(host.Sign(bytes)),
            host.PublicKeySpki);
    }
}
