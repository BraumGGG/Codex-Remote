using System.Text;
using CodexBridge.Host.Remote;

namespace CodexBridge.Host.Tests;

public sealed class SignedSignalPayloadTests
{
    [Fact]
    public async Task Sign_VerifiesExactPayloadBytes()
    {
        var directory = TestPaths.CreateDirectory("signal-sign");
        try
        {
            using var identity = await new RemoteIdentityStore(
                Path.Combine(directory, "identity.json")).GetOrCreateAsync();
            var payload = Encoding.UTF8.GetBytes(
                "{\"sessionId\":\"AQIDBAUGBwgJCgsMDQ4PEA\",\"sdp\":\"a=fingerprint:sha-256 AA:BB\\r\\n\"}");

            var signed = SignedSignalPayload.Sign(payload, identity);

            Assert.True(signed.Verify(identity.PublicKeySpki));
            Assert.Equal(payload, signed.GetPayloadBytes());

            var modifiedPayload = Encoding.UTF8.GetBytes(
                "{\"sessionId\":\"AQIDBAUGBwgJCgsMDQ4PEA\",\"sdp\":\"a=fingerprint:sha-256 AA:BC\\r\\n\"}");
            var modified = signed with
            {
                PayloadBase64Url = RemoteEncoding.Base64UrlEncode(modifiedPayload),
            };
            Assert.False(modified.Verify(identity.PublicKeySpki));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Verify_RejectsSignatureFromAnotherHost()
    {
        var firstDirectory = TestPaths.CreateDirectory("signal-first");
        var secondDirectory = TestPaths.CreateDirectory("signal-second");
        try
        {
            using var first = await new RemoteIdentityStore(
                Path.Combine(firstDirectory, "identity.json")).GetOrCreateAsync();
            using var second = await new RemoteIdentityStore(
                Path.Combine(secondDirectory, "identity.json")).GetOrCreateAsync();
            var signed = SignedSignalPayload.Sign("offer"u8.ToArray(), first);

            Assert.False(signed.Verify(second.PublicKeySpki));
        }
        finally
        {
            if (Directory.Exists(firstDirectory)) Directory.Delete(firstDirectory, true);
            if (Directory.Exists(secondDirectory)) Directory.Delete(secondDirectory, true);
        }
    }
}
