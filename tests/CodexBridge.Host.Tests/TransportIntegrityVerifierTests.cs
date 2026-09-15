using System.Security.Cryptography;
using System.Text.Json;
using CodexBridge.Host.Remote;

namespace CodexBridge.Host.Tests;

public sealed class TransportIntegrityVerifierTests : IDisposable
{
    private readonly string _directory = TestPaths.CreateDirectory("transport-integrity");

    [Fact]
    public void Verify_AcceptsMatchingDevelopmentManifest()
    {
        var (executable, manifest) = CreateFiles("transport-v1"u8.ToArray());
        var verifier = new TransportIntegrityVerifier(new StubSignatureVerifier(false));

        var result = verifier.Verify(executable, manifest, requireAuthenticode: false);

        Assert.True(result.IsValid);
        Assert.Equal(1, result.ProtocolVersion);
    }

    [Fact]
    public void Verify_RejectsMissingAndTamperedExecutable()
    {
        var (executable, manifest) = CreateFiles("transport-v1"u8.ToArray());
        File.WriteAllBytes(executable, "tampered"u8.ToArray());
        var verifier = new TransportIntegrityVerifier(new StubSignatureVerifier(true));

        Assert.Equal("hash_mismatch", verifier.Verify(executable, manifest, false).ErrorCode);
        File.Delete(executable);
        Assert.Equal("executable_missing", verifier.Verify(executable, manifest, false).ErrorCode);
    }

    [Fact]
    public void Verify_RequiresAuthenticodeInProduction()
    {
        var (executable, manifest) = CreateFiles("transport-v1"u8.ToArray());

        Assert.Equal("signature_invalid", new TransportIntegrityVerifier(
            new StubSignatureVerifier(false)).Verify(executable, manifest, true).ErrorCode);
        Assert.True(new TransportIntegrityVerifier(
            new StubSignatureVerifier(true)).Verify(executable, manifest, true).IsValid);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    public void AuthenticodePolicy_RequiresExplicitOptIn(string? configured, bool expected)
    {
        Assert.Equal(expected, RemoteAccessOptions.ShouldRequireAuthenticode(configured));
    }

    private (string Executable, string Manifest) CreateFiles(byte[] content)
    {
        Directory.CreateDirectory(_directory);
        var executable = Path.Combine(_directory, "CodexBridge.Transport.exe");
        var manifest = Path.Combine(_directory, "transport-manifest.json");
        File.WriteAllBytes(executable, content);
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            ProtocolVersion = 1,
            FileName = Path.GetFileName(executable),
            Sha256 = Convert.ToHexString(SHA256.HashData(content)),
        }));
        return (executable, manifest);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class StubSignatureVerifier(bool result) : IAuthenticodeVerifier
    {
        public bool IsTrusted(string path) => result;
    }
}
