using System.Security.Cryptography;

namespace CodexBridge.Signal;

public sealed record HostChallenge(string HostId, byte[] PublicKeySpki, byte[] Challenge);

public static class HostChallengeVerifier
{
    public static HostChallenge Create(string hostId, string publicKeySpki)
    {
        try
        {
            var keyBytes = SignalEncoding.Decode(publicKeySpki);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(keyBytes, out var read);
            var derivedHostId = SignalEncoding.Encode(SHA256.HashData(keyBytes));
            if (read != keyBytes.Length ||
                key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value ||
                !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(derivedHostId),
                    System.Text.Encoding.ASCII.GetBytes(hostId)))
                throw new InvalidDataException("Host identity is invalid.");
            return new HostChallenge(hostId, keyBytes, RandomNumberGenerator.GetBytes(32));
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or ArgumentException)
        {
            throw new InvalidDataException("Host identity is invalid.", exception);
        }
    }

    public static bool Verify(HostChallenge challenge, string signatureBase64Url)
        => VerifyProof(challenge.PublicKeySpki, challenge.Challenge, signatureBase64Url);

    public static bool VerifyProof(byte[] publicKeySpki, byte[] challenge, string signatureBase64Url)
    {
        try
        {
            var signature = SignalEncoding.Decode(signatureBase64Url);
            if (signature.Length != 64)
                return false;
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKeySpki, out var read);
            return read == publicKeySpki.Length &&
                key.ExportParameters(false).Curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value &&
                key.VerifyData(
                challenge,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}
