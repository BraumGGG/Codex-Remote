using System.Security.Cryptography;
using System.Text.Json;

namespace CodexBridge.Entitlements;

public enum EntitlementState
{
    Free,
    Pro,
    OfflineGrace,
    Expired,
    Invalid,
}

public sealed record EntitlementGrant(
    int Version,
    string KeyId,
    string LicenseId,
    string UserId,
    string HostId,
    string DeviceId,
    string Plan,
    DateTimeOffset IssuedAt,
    DateTimeOffset RefreshAfter,
    DateTimeOffset ExpiresAt,
    DateTimeOffset OfflineUntil);

public sealed record EntitlementDecision(
    bool CanSend,
    EntitlementState State,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? OfflineUntil,
    string? ErrorCode)
{
    public static EntitlementDecision Free(string? errorCode = null) =>
        new(false, errorCode is null ? EntitlementState.Free : EntitlementState.Invalid, null, null, errorCode);
}

public sealed class EntitlementTokenSigner(ECDsa privateKey, string keyId)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Sign(EntitlementGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (!string.Equals(grant.KeyId, keyId, StringComparison.Ordinal))
            throw new ArgumentException("Grant key ID does not match signer.", nameof(grant));
        EntitlementTokenVerifier.ValidateGrantShape(grant);
        var payload = JsonSerializer.SerializeToUtf8Bytes(grant, JsonOptions);
        if (payload.Length > EntitlementTokenVerifier.MaximumPayloadBytes)
            throw new ArgumentException("Grant payload is too large.", nameof(grant));
        var signature = privateKey.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{Base64Url.Encode(payload)}.{Base64Url.Encode(signature)}";
    }
}

public sealed class EntitlementTokenVerifier
{
    internal const int MaximumPayloadBytes = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumOfflineGrace = TimeSpan.FromHours(72);
    private readonly IReadOnlyDictionary<string, byte[]> _publicKeys;
    private readonly TimeProvider _timeProvider;

    public EntitlementTokenVerifier(
        IReadOnlyDictionary<string, string> publicKeys,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(publicKeys);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _publicKeys = publicKeys.ToDictionary(
            item => item.Key,
            item => Base64Url.Decode(item.Value, 1024),
            StringComparer.Ordinal);
    }

    public EntitlementDecision Verify(string? token, string expectedHostId, string expectedDeviceId)
    {
        if (string.IsNullOrWhiteSpace(token)) return EntitlementDecision.Free();
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 2) return EntitlementDecision.Free("entitlement_invalid");
            var payload = Base64Url.Decode(parts[0], MaximumPayloadBytes);
            var signature = Base64Url.Decode(parts[1], 128);
            if (signature.Length != 64) return EntitlementDecision.Free("entitlement_invalid");
            var grant = JsonSerializer.Deserialize<EntitlementGrant>(payload, JsonOptions);
            if (grant is null) return EntitlementDecision.Free("entitlement_invalid");
            ValidateGrantShape(grant);
            if (!_publicKeys.TryGetValue(grant.KeyId, out var publicKey))
                return EntitlementDecision.Free("entitlement_unknown_key");
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out var read);
            if (read != publicKey.Length || !key.VerifyData(
                    payload,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return EntitlementDecision.Free("entitlement_invalid_signature");
            if (!string.Equals(grant.HostId, expectedHostId, StringComparison.Ordinal) ||
                !string.Equals(grant.DeviceId, expectedDeviceId, StringComparison.Ordinal))
                return EntitlementDecision.Free("entitlement_binding_mismatch");
            var now = _timeProvider.GetUtcNow();
            if (grant.IssuedAt > now + ClockSkew ||
                grant.RefreshAfter < grant.IssuedAt ||
                grant.ExpiresAt < grant.RefreshAfter ||
                grant.OfflineUntil < grant.ExpiresAt ||
                grant.OfflineUntil - grant.ExpiresAt > MaximumOfflineGrace)
                return EntitlementDecision.Free("entitlement_time_invalid");
            if (!string.Equals(grant.Plan, "pro", StringComparison.Ordinal))
                return new EntitlementDecision(false, EntitlementState.Free, grant.ExpiresAt, grant.OfflineUntil, null);
            if (now <= grant.ExpiresAt)
                return new EntitlementDecision(true, EntitlementState.Pro, grant.ExpiresAt, grant.OfflineUntil, null);
            if (now <= grant.OfflineUntil)
                return new EntitlementDecision(true, EntitlementState.OfflineGrace, grant.ExpiresAt, grant.OfflineUntil, null);
            return new EntitlementDecision(false, EntitlementState.Expired, grant.ExpiresAt, grant.OfflineUntil, "entitlement_expired");
        }
        catch (Exception exception) when (exception is FormatException or JsonException or CryptographicException or ArgumentException)
        {
            return EntitlementDecision.Free("entitlement_invalid");
        }
    }

    internal static void ValidateGrantShape(EntitlementGrant grant)
    {
        if (grant.Version != 1 ||
            !IsIdentifier(grant.KeyId, 64) ||
            !IsIdentifier(grant.LicenseId, 128) ||
            !IsIdentifier(grant.UserId, 128) ||
            !IsIdentifier(grant.HostId, 128) ||
            !IsIdentifier(grant.DeviceId, 128) ||
            grant.Plan is not ("free" or "pro"))
            throw new ArgumentException("Grant shape is invalid.", nameof(grant));
    }

    private static bool IsIdentifier(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}

internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string value, int maximumBytes)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maximumBytes * 2 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new FormatException("Invalid base64url value.");
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => throw new FormatException("Invalid base64url length.") };
        var bytes = Convert.FromBase64String(padded);
        if (bytes.Length > maximumBytes) throw new FormatException("Decoded value is too large.");
        return bytes;
    }
}
