namespace CodexBridge.Host.Remote;

public sealed record SignedSignalPayload(string PayloadBase64Url, string SignatureBase64Url)
{
    public static SignedSignalPayload Sign(ReadOnlySpan<byte> payload, RemoteHostIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new SignedSignalPayload(
            RemoteEncoding.Base64UrlEncode(payload),
            RemoteEncoding.Base64UrlEncode(identity.Sign(payload)));
    }

    public byte[] GetPayloadBytes() => RemoteEncoding.Base64UrlDecode(PayloadBase64Url);

    public bool Verify(string publicKeySpki)
    {
        try
        {
            return RemoteHostIdentity.Verify(
                publicKeySpki,
                GetPayloadBytes(),
                RemoteEncoding.Base64UrlDecode(SignatureBase64Url));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
